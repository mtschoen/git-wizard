using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib;

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

    public event Action<string>? Stopped;

    /// <summary>
    /// Per-drive scan failures from the most recent <see cref="RunAsync"/>'s arm step, keyed
    /// by drive letter. Set before the first event is yielded. A drive present here was not
    /// armed and is not watched; watching continues on the drives that armed successfully.
    /// </summary>
    public IReadOnlyDictionary<string, string> ScanErrors { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
        var filtersByVolume = await BuildFiltersByVolumeAsync(ct).ConfigureAwait(false);
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
            await pumpTask.ConfigureAwait(false);
        }
    }

    // Owns the linked CancellationTokenSource used to fold source death into cancellation.
    // The SourceDied handler is an instance method reading instance fields, not a closure
    // capturing a local that this type later disposes - keeping the source-death wiring out
    // of RunAsync's own scope avoids a disposed-closure hazard there. Takes the death callback
    // directly (rather than exposing its own event) since RunAsync is its only caller and
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

        void OnSourceDied(string reason)
        {
            _onDied(reason);
            _cts.Cancel();
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
        ScanErrors = armResult.Errors;

        var filters = new Dictionary<string, RepositoryChangeFilter>(StringComparer.OrdinalIgnoreCase);
        foreach (var volume in volumes)
        {
            trackedByVolume.TryGetValue(volume, out var tracked);
            searchByVolume.TryGetValue(volume, out var search);
            filters[volume] = new RepositoryChangeFilter(
                Array.Empty<ScanRecord>(), tracked ?? [], search ?? []);
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

    static string? GetVolume(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? null : root[..1];
    }

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

        while (true)
        {
            var winner = await Task.WhenAny(nextBatch, idle).ConfigureAwait(false);
            if (winner == nextBatch)
            {
                if (!await nextBatch.ConfigureAwait(false))
                    break;

                ApplyBatch(filtersByVolume, enumerator.Current, pending);
                nextBatch = enumerator.MoveNextAsync().AsTask();
                idle = Task.Delay(_debounce, ct);
                continue;
            }

            await idle.ConfigureAwait(false);
            await FlushAsync(pending, writer, ct).ConfigureAwait(false);
            idle = Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        await FlushAsync(pending, writer, ct).ConfigureAwait(false);
    }

    static void ApplyBatch(
        IReadOnlyDictionary<string, RepositoryChangeFilter> filtersByVolume,
        VolumeChangeBatch batch,
        PendingChanges pending)
    {
        if (filtersByVolume.TryGetValue(batch.Volume, out var filter))
            pending.Merge(filter.Classify(batch.Entries));
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
        readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);

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
