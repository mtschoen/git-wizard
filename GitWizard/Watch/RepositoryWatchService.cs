using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using JetBrains.Annotations;

namespace GitWizard.Watch;

/// <summary>
/// Watches a set of volumes for filesystem changes via an <see cref="IVolumeChangeSource"/>
/// and maps them to <see cref="RepositoryChangeEvent"/>s for the tracked/search repository
/// roots. Repeated changes to the same root within one debounce window coalesce into a
/// single <see cref="RepositoryChangeKind.Changed"/> event. Rename correlation (matching a
/// <see cref="RepositoryChangeKind.Deleted"/> against a <see cref="RepositoryChangeKind.Created"/>)
/// is deliberately left to the consumer - this service never inspects git remotes.
/// </summary>
public sealed class RepositoryWatchService
{
    readonly IVolumeChangeSource _source;
    readonly IReadOnlyCollection<string> _trackedRoots;
    readonly IReadOnlyCollection<string> _searchRoots;
    readonly TimeSpan _debounce;

    readonly Lock _errorsLock = new();
    FrozenDictionary<string, string> _scanErrors = FrozenDictionary<string, string>.Empty;

    public event Action? ScanErrorsChanged;
    public event Action? Started;
    public event Action<string>? Stopped;

    /// <summary>
    /// Immutable snapshot of initial scan failures and subsequent watch failures, keyed by drive.
    /// A failed watch does not imply that the drive's index is unavailable for queries.
    /// </summary>
    [UsedImplicitly]
    public IReadOnlyDictionary<string, string> ScanErrors
    {
        get
        {
            lock (_errorsLock)
                return _scanErrors;
        }
    }

    public RepositoryWatchService(
        IVolumeChangeSource source,
        IReadOnlyCollection<string> trackedRoots,
        IReadOnlyCollection<string> searchRoots,
        TimeSpan? debounce = null)
    {
        _source = source;
        _trackedRoots = trackedRoots;
        _searchRoots = searchRoots;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
    }

    public async IAsyncEnumerable<RepositoryChangeEvent> RunAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        lock (_errorsLock)
            _scanErrors = FrozenDictionary<string, string>.Empty;
        var diagnostics = _source as IVolumeChangeDiagnostics;
        if (diagnostics is not null)
            diagnostics.DriveFailed += OnDriveFailed;
        try
        {
            await foreach (var changeEvent in RunCoreAsync(ct).ConfigureAwait(false))
                yield return changeEvent;
        }
        finally
        {
            if (diagnostics is not null)
                diagnostics.DriveFailed -= OnDriveFailed;
        }
    }

    async IAsyncEnumerable<RepositoryChangeEvent> RunCoreAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var filtersByVolume = await BuildFiltersByVolumeAsync(ct).ConfigureAwait(false);
        Started?.Invoke();
        var channel = Channel.CreateUnbounded<RepositoryChangeEvent>();
        using var deathSignal = new SourceDeathSignal(
            _source,
            reason =>
            {
                Stopped?.Invoke(reason);
                channel.Writer.TryComplete();
            },
            ct);

        var pumpTask = PumpAsync(filtersByVolume, channel.Writer, deathSignal.Token);

        try
        {
            await foreach (var changeEvent in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return changeEvent;
        }
        finally
        {
            // An early consumer exit must stop the pending source read before awaiting the pump.
            await deathSignal.CancelAsync().ConfigureAwait(false);
            await pumpTask.ConfigureAwait(false);
        }
    }

    void OnDriveFailed(string drive, string message)
    {
        lock (_errorsLock)
        {
            if (_scanErrors.TryGetValue(drive, out var previous) && previous == message)
                return;
            var errors = new Dictionary<string, string>(_scanErrors, StringComparer.OrdinalIgnoreCase)
            {
                [drive] = message
            };
            _scanErrors = errors.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
        ScanErrorsChanged?.Invoke();
    }

    void SetArmErrors(IReadOnlyDictionary<string, string> armErrors)
    {
        lock (_errorsLock)
        {
            // A diagnostic raised during arming is newer than the initial scan result.
            var errors = new Dictionary<string, string>(armErrors, StringComparer.OrdinalIgnoreCase);
            foreach (var (drive, message) in _scanErrors)
                errors[drive] = message;
            _scanErrors = errors.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
        ScanErrorsChanged?.Invoke();
    }

    // Owns the linked CancellationTokenSource used to fold source death into cancellation.
    // The SourceDied handler is an instance method reading instance fields, not a closure
    // capturing a local that this type later disposes - keeping the source-death wiring out
    // of RunCoreAsync's own scope avoids a disposed-closure hazard there. Takes the death callback
    // directly (rather than exposing its own event) since RunCoreAsync is its only caller and
    // always attaches exactly one handler - a nullable event here would just be an
    // unreachable null-check branch.
    sealed class SourceDeathSignal : IDisposable
    {
        readonly IVolumeChangeSource _source;
        readonly CancellationTokenSource _cts;
        readonly Action<string> _onDied;

        public SourceDeathSignal(IVolumeChangeSource source, Action<string> onDied, CancellationToken ct)
        {
            _source = source;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _onDied = onDied;
            _source.SourceDied += OnSourceDied;
        }

        public CancellationToken Token => _cts.Token;
        public Task CancelAsync() => _cts.CancelAsync();

        void OnSourceDied(string reason)
        {
            _onDied(reason);
            _ = _cts.CancelAsync();
        }

        public void Dispose()
        {
            _source.SourceDied -= OnSourceDied;
            _cts.Cancel();
            _cts.Dispose();
        }
    }

    async Task<Dictionary<string, RepositoryChangeFilter>> BuildFiltersByVolumeAsync(CancellationToken ct)
    {
        var trackedByVolume = GroupRootsByVolume(_trackedRoots);
        var searchByVolume = GroupRootsByVolume(_searchRoots);
        var volumes = trackedByVolume.Keys
            .Union(searchByVolume.Keys, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var armResult = await _source.ArmAndCatchUpAsync(volumes, ct).ConfigureAwait(false);
        SetArmErrors(armResult.Errors);

        var filters = new Dictionary<string, RepositoryChangeFilter>(StringComparer.OrdinalIgnoreCase);
        foreach (var volume in volumes)
        {
            trackedByVolume.TryGetValue(volume, out var tracked);
            searchByVolume.TryGetValue(volume, out var search);
            filters[volume] = new RepositoryChangeFilter(tracked ?? [], search ?? []);
        }

        return filters;
    }

    static Dictionary<string, List<string>> GroupRootsByVolume(IReadOnlyCollection<string> roots)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var volume = GetVolume(root);
            if (volume is null)
                continue;

            if (!result.TryGetValue(volume, out var list))
                result[volume] = list = [];

            list.Add(root);
        }

        return result;
    }

    static string? GetVolume(string path) => WatchPath.Parse(path).Volume;

    async Task PumpAsync(
        IReadOnlyDictionary<string, RepositoryChangeFilter> filtersByVolume,
        ChannelWriter<RepositoryChangeEvent> writer,
        CancellationToken ct)
    {
        var pending = new PendingChanges();
        try
        {
            await using var enumerator = _source.WatchAsync(ct).GetAsyncEnumerator(ct);
            await PumpLoopAsync(enumerator, filtersByVolume, pending, writer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on RunAsync cancellation or source death; nothing left to flush.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    async Task PumpLoopAsync(
        IAsyncEnumerator<VolumeChangeBatch> enumerator,
        IReadOnlyDictionary<string, RepositoryChangeFilter> filtersByVolume,
        PendingChanges pending,
        ChannelWriter<RepositoryChangeEvent> writer,
        CancellationToken ct)
    {
        var nextBatch = enumerator.MoveNextAsync().AsTask();
        var idle = Task.Delay(Timeout.InfiniteTimeSpan, ct);
        var flushScheduled = false;

        try
        {
            while (true)
            {
                var winner = await Task.WhenAny(nextBatch, idle).ConfigureAwait(false);
                if (winner == nextBatch)
                {
                    if (!await nextBatch.ConfigureAwait(false))
                        break;

                    var hasRelevantChanges = ApplyBatch(filtersByVolume, enumerator.Current, pending);
                    nextBatch = enumerator.MoveNextAsync().AsTask();
                    if (hasRelevantChanges && !flushScheduled)
                    {
                        idle = Task.Delay(_debounce, ct);
                        flushScheduled = true;
                    }
                    continue;
                }

                await idle.ConfigureAwait(false);
                await FlushAsync(pending, writer, ct).ConfigureAwait(false);
                idle = Task.Delay(Timeout.InfiniteTimeSpan, ct);
                flushScheduled = false;
            }
        }
        finally
        {
            // If idle won the race above (or ct cancelled between iterations), nextBatch's
            // MoveNextAsync may still be outstanding when this method returns/throws. The
            // caller's `await using` then disposes the enumerator immediately - and a
            // compiler-generated async-iterator enumerator throws NotSupportedException if
            // DisposeAsync races a still-in-flight MoveNextAsync. Observing it here first
            // (swallowing only the expected cancellation) makes that always safe.
            await ObserveCancellationAsync(nextBatch).ConfigureAwait(false);
        }

        await FlushAsync(pending, writer, ct).ConfigureAwait(false);
    }

    static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: this task shares the same cancellation token as the loop that awaited it.
        }
    }

    static bool ApplyBatch(
        IReadOnlyDictionary<string, RepositoryChangeFilter> filtersByVolume,
        VolumeChangeBatch batch,
        PendingChanges pending)
    {
        if (!filtersByVolume.TryGetValue(batch.Volume, out var filter))
            return false;

        var result = filter.Classify(batch.Entries);
        pending.Merge(result);
        return result.Changed.Count > 0 || result.Created.Count > 0 || result.Deleted.Count > 0;
    }

    static async Task FlushAsync(
        PendingChanges pending, ChannelWriter<RepositoryChangeEvent> writer, CancellationToken ct)
    {
        foreach (var changeEvent in pending.DrainEvents())
            await writer.WriteAsync(changeEvent, ct).ConfigureAwait(false);
    }

    // Accumulates one debounce window's worth of classified roots. Changed roots dedupe
    // across every batch merged into the window, matching the "single Changed per window"
    // coalescing contract; Created/Deleted roots dedupe the same way rather than being
    // treated as ordered events, since a repository root is only meaningfully created or
    // deleted once per window.
    sealed class PendingChanges
    {
        readonly HashSet<string> _changed = new(WatchPath.StringComparer);
        readonly HashSet<string> _created = new(WatchPath.StringComparer);
        readonly HashSet<string> _deleted = new(WatchPath.StringComparer);

        public void Merge(FilterResult result)
        {
            _changed.UnionWith(result.Changed);
            _created.UnionWith(result.Created);
            _deleted.UnionWith(result.Deleted);
        }

        public IEnumerable<RepositoryChangeEvent> DrainEvents()
        {
            foreach (var root in _changed)
                yield return new RepositoryChangeEvent(root, RepositoryChangeKind.Changed);
            foreach (var root in _created)
                yield return new RepositoryChangeEvent(root, RepositoryChangeKind.Created);
            foreach (var root in _deleted)
                yield return new RepositoryChangeEvent(root, RepositoryChangeKind.Deleted);

            _changed.Clear();
            _created.Clear();
            _deleted.Clear();
        }
    }
}
