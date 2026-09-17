using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using JetBrains.Annotations;

namespace GitWizard.Watch;

/// <summary>
/// Watches a set of volumes for filesystem changes via an <see cref="IVolumeChangeSource"/>
/// and maps them to <see cref="RepositoryChangeEvent"/>s for the tracked/search repository
/// roots. Repeated changes to the same root within one debounce window coalesce into a
/// single <see cref="RepositoryChangeKind.Changed"/> event. A repository whose .git directory
/// creation classifies under a search root is adopted into that volume's running filter, so
/// its later edits classify as <see cref="RepositoryChangeKind.Changed"/> without a watch
/// restart. Rename correlation (matching a <see cref="RepositoryChangeKind.Deleted"/> against
/// a <see cref="RepositoryChangeKind.Created"/>) is deliberately left to the consumer - this
/// service never inspects git remotes.
/// </summary>
public sealed class RepositoryWatchService
{
    readonly IVolumeChangeSource _source;
    readonly IReadOnlyCollection<string> _trackedRoots;
    readonly IReadOnlyCollection<string> _searchRoots;
    readonly TimeSpan _debounce;

    readonly Lock _errorsLock = new();
    FrozenDictionary<string, string> _scanErrors = FrozenDictionary<string, string>.Empty;
    FrozenSet<JournalWarning> _scanWarnings = FrozenSet<JournalWarning>.Empty;

    public event Action? ScanErrorsChanged;
    public event Action? ScanWarningsChanged;
    public event Action? Started;
    public event Action<string>? Stopped;
    public event Action<VolumeScanProgress>? ScanProgress;

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

    [UsedImplicitly]
    public IReadOnlyCollection<JournalWarning> ScanWarnings
    {
        get
        {
            lock (_errorsLock)
                return _scanWarnings;
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
        {
            _scanErrors = FrozenDictionary<string, string>.Empty;
            _scanWarnings = FrozenSet<JournalWarning>.Empty;
        }
        var diagnostics = _source as IVolumeChangeDiagnostics;
        if (diagnostics is not null)
            diagnostics.DriveFailed += OnDriveFailed;
        var scanProgress = _source as IVolumeScanProgress;
        if (scanProgress is not null)
            scanProgress.ScanProgress += OnScanProgress;
        try
        {
            await foreach (var changeEvent in RunCoreAsync(ct).ConfigureAwait(false))
                yield return changeEvent;
        }
        finally
        {
            if (diagnostics is not null)
                diagnostics.DriveFailed -= OnDriveFailed;
            if (scanProgress is not null)
                scanProgress.ScanProgress -= OnScanProgress;
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

    void OnScanProgress(VolumeScanProgress sample) => ScanProgress?.Invoke(sample);

    void SetArmResult(VolumeArmResult armResult)
    {
        lock (_errorsLock)
        {
            // A diagnostic raised during arming is newer than the initial scan result.
            var errors = new Dictionary<string, string>(armResult.Errors, StringComparer.OrdinalIgnoreCase);
            foreach (var (drive, message) in _scanErrors)
                errors[drive] = message;
            _scanErrors = errors.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

            var warnings = armResult.JournalWarnings
                .GroupBy(warning => warning.DriveLetter, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last());
            _scanWarnings = warnings.ToFrozenSet();
        }
        ScanErrorsChanged?.Invoke();
        ScanWarningsChanged?.Invoke();
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
        SetArmResult(armResult);

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

        var hasRelevantChanges = false;
        foreach (var entry in batch.Entries)
        {
            var classification = filter.ClassifyEntry(entry);
            if (classification is null)
                continue;

            hasRelevantChanges = true;
            var (kind, root) = classification.Value;
            switch (kind)
            {
                case RepositoryChangeKind.Created:
                    filter.AddRepositoryRoot(root);
                    pending.Add(root, RepositoryChangeKind.Created);
                    break;
                case RepositoryChangeKind.Deleted:
                    filter.RemoveRepositoryRoot(root);
                    pending.Add(root, RepositoryChangeKind.Deleted);
                    break;
                case RepositoryChangeKind.Changed:
                    pending.Add(root, RepositoryChangeKind.Changed);
                    break;
            }
        }

        return hasRelevantChanges;
    }

    static async Task FlushAsync(
        PendingChanges pending, ChannelWriter<RepositoryChangeEvent> writer, CancellationToken ct)
    {
        foreach (var changeEvent in pending.DrainEvents())
            await writer.WriteAsync(changeEvent, ct).ConfigureAwait(false);
    }

    // Accumulates one debounce window's worth of classified events. Repeated Changed
    // events for the same root coalesce into a single Changed event per debounce window
    // (or per lifecycle phase), while lifecycle transitions (Created/Deleted) are emitted
    // in the exact order they occurred.
    sealed class PendingChanges
    {
        sealed class RootPendingState
        {
            public RepositoryChangeKind? LastEvent { get; set; }
            public bool HasChangedSinceLifecycle { get; set; }
        }

        readonly List<RepositoryChangeEvent> _events = [];
        readonly Dictionary<string, RootPendingState> _stateByRoot = new(WatchPath.StringComparer);

        public void Add(string root, RepositoryChangeKind kind)
        {
            if (!_stateByRoot.TryGetValue(root, out var state))
            {
                state = new RootPendingState();
                _stateByRoot[root] = state;
            }

            switch (kind)
            {
                case RepositoryChangeKind.Created:
                    if (state.LastEvent == RepositoryChangeKind.Created)
                        return;

                    state.LastEvent = RepositoryChangeKind.Created;
                    state.HasChangedSinceLifecycle = false;
                    _events.Add(new RepositoryChangeEvent(root, RepositoryChangeKind.Created));
                    break;

                case RepositoryChangeKind.Deleted:
                    if (state.LastEvent == RepositoryChangeKind.Deleted)
                        return;

                    state.LastEvent = RepositoryChangeKind.Deleted;
                    state.HasChangedSinceLifecycle = false;
                    _events.Add(new RepositoryChangeEvent(root, RepositoryChangeKind.Deleted));
                    break;

                case RepositoryChangeKind.Changed:
                    if (state.HasChangedSinceLifecycle)
                        return;

                    state.LastEvent = RepositoryChangeKind.Changed;
                    state.HasChangedSinceLifecycle = true;
                    _events.Add(new RepositoryChangeEvent(root, RepositoryChangeKind.Changed));
                    break;
            }
        }

        public RepositoryChangeEvent[] DrainEvents()
        {
            var events = _events.ToArray();
            _events.Clear();
            _stateByRoot.Clear();
            return events;
        }
    }
}
