using System.Threading.Channels;

namespace GitWizard.Watch;

public sealed partial class RepositoryWatchService
{
    async Task PumpAsync(
        IReadOnlyDictionary<string, RepositoryChangeFilter> filtersByVolume,
        ChannelWriter<RepositoryChangeEvent> writer, CancellationToken ct)
    {
        try
        {
            await using var enumerator = _source.WatchAsync(ct).GetAsyncEnumerator(ct);
            var pending = new PendingChanges();
            // Batches buffered while discovery armed and reconciled membership are classified
            // first, in journal order, through the same path as live batches - a Modified
            // event for a tracked repository or a delete/create rename pair captured during
            // catch-up must reach the consumer, not vanish with the buffer.
            if (_startup is { StartupBatches.Count: > 0 })
            {
                foreach (var batch in _startup.StartupBatches)
                    ApplyBatch(filtersByVolume, batch, pending);
                await FlushAsync(pending, filtersByVolume, writer, ct).ConfigureAwait(false);
            }
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
        PendingChanges pending, ChannelWriter<RepositoryChangeEvent> writer, CancellationToken ct)
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
                await FlushAsync(pending, filtersByVolume, writer, ct).ConfigureAwait(false);
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

        await FlushAsync(pending, filtersByVolume, writer, ct).ConfigureAwait(false);
    }

    static async Task ObserveCancellationAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Expected: task shares the same cancellation token as the loop that awaited it.
        }
    }

    static bool ApplyBatch(
        IReadOnlyDictionary<string, RepositoryChangeFilter> filtersByVolume,
        VolumeChangeBatch batch, PendingChanges pending)
    {
        if (!filtersByVolume.TryGetValue(batch.Volume, out var filter))
            return false;

        var hasRelevantChanges = false;
        foreach (var entry in batch.Entries)
        {
            if (filter.TryClassifyMovedInCandidate(entry, out var candidate))
            {
                hasRelevantChanges = true;
                pending.AddMovedInCandidate(candidate);
                continue;
            }

            if (entry.Kind == VolumeEntryKind.MovedIn)
            {
                var path = WatchPath.Parse(entry.FullPath);
                var movedInCandidate = path.TryGetGitDirectoryParent(out var parent) ? parent : path;
                if (pending.HasDeletedRoot(movedInCandidate))
                {
                    hasRelevantChanges = true;
                    pending.AddMovedInCandidate(movedInCandidate.Original);
                    continue;
                }
            }

            var classification = filter.ClassifyEntry(entry);
            if (classification is null)
                continue;

            hasRelevantChanges = true;
            var (kind, root) = classification.Value;
            if (kind == RepositoryChangeKind.Created)
                filter.AddRepositoryRoot(root);
            else if (kind == RepositoryChangeKind.Deleted)
                filter.RemoveRepositoryRoot(root);
            pending.Add(root, kind);
        }

        return hasRelevantChanges;
    }

    static async Task FlushAsync(
        PendingChanges pending,
        IReadOnlyDictionary<string, RepositoryChangeFilter> filtersByVolume,
        ChannelWriter<RepositoryChangeEvent> writer, CancellationToken ct)
    {
        var createdRoots = new HashSet<string>(WatchPath.StringComparer);
        foreach (var changeEvent in pending.DrainEvents())
        {
            if (changeEvent.Kind == RepositoryChangeKind.Created)
                createdRoots.Add(changeEvent.RepoRoot);
            else if (changeEvent.Kind == RepositoryChangeKind.Deleted)
                createdRoots.Remove(changeEvent.RepoRoot);
            await writer.WriteAsync(changeEvent, ct).ConfigureAwait(false);
        }

        // Candidates flush after the window's regular events, so a same-window rename delivers
        // Deleted(old) before the probe-emitted Created(new) - the UI's pending-delete rename
        // correlation depends on that order. Ignored and temp-excluded candidates were already
        // dropped by RepositoryChangeFilter.TryClassifyMovedInCandidate, so only the repository
        // probe remains here.
        foreach (var candidate in pending.DrainMovedInCandidates())
        {
            if (createdRoots.Contains(candidate) || !LooksLikeRepository(candidate))
                continue;

            var volume = WatchPath.Parse(candidate).Volume;
            if (volume is not null && filtersByVolume.TryGetValue(volume, out var filter))
                filter.AddRepositoryRoot(candidate);

            createdRoots.Add(candidate);
            await writer.WriteAsync(new RepositoryChangeEvent(candidate, RepositoryChangeKind.Created), ct).ConfigureAwait(false);
        }
    }

    // A gitfile worktree or submodule has .git as a file, not a directory.
    static bool LooksLikeRepository(string path)
    {
        var gitPath = Path.Combine(path, ".git");
        return Directory.Exists(gitPath) || File.Exists(gitPath);
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
        readonly HashSet<string> _movedInCandidates = new(WatchPath.StringComparer);

        public void AddMovedInCandidate(string candidate) => _movedInCandidates.Add(candidate);

        public bool HasDeletedRoot(WatchPath candidate) =>
            _stateByRoot.Any(pair => pair.Value.LastEvent == RepositoryChangeKind.Deleted && candidate.IsEqualTo(WatchPath.Parse(pair.Key)));

        public void Add(string root, RepositoryChangeKind kind)
        {
            if (!_stateByRoot.TryGetValue(root, out var state))
                _stateByRoot[root] = state = new RootPendingState();

            if (kind == RepositoryChangeKind.Changed)
            {
                if (state.HasChangedSinceLifecycle)
                    return;
                state.LastEvent = RepositoryChangeKind.Changed;
                state.HasChangedSinceLifecycle = true;
                _events.Add(new RepositoryChangeEvent(root, RepositoryChangeKind.Changed));
                return;
            }

            if (state.LastEvent == kind)
                return;
            state.LastEvent = kind;
            state.HasChangedSinceLifecycle = false;
            _events.Add(new RepositoryChangeEvent(root, kind));
        }

        public RepositoryChangeEvent[] DrainEvents()
        {
            var events = _events.ToArray();
            _events.Clear();
            _stateByRoot.Clear();
            return events;
        }

        // Drained after DrainEvents; the flush probes these paths for a .git entry before
        // turning them into Created events.
        public string[] DrainMovedInCandidates()
        {
            var candidates = _movedInCandidates.ToArray();
            _movedInCandidates.Clear();
            return candidates;
        }
    }
}
