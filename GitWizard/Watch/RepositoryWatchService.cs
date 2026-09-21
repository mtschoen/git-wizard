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
/// creation classifies under a search root (or is probed from a MovedIn candidate) is adopted
/// into that volume's running filter, so its later edits classify as
/// <see cref="RepositoryChangeKind.Changed"/> without a watch restart. Rename correlation
/// (matching a <see cref="RepositoryChangeKind.Deleted"/> against a
/// <see cref="RepositoryChangeKind.Created"/>) is deliberately left to the consumer - this
/// service never inspects git remotes. Directory renames arrive as Deleted + MovedIn: an
/// untracked MovedIn path under a search root is probed once at flush time for a .git entry
/// (directory or gitfile) and surfaces as Created on a hit.
/// </summary>
public sealed class RepositoryWatchService
{
    readonly IVolumeChangeSource _source;
    readonly IReadOnlyCollection<string> _trackedRoots;
    readonly IReadOnlyCollection<string> _searchRoots;
    readonly IReadOnlyCollection<string> _ignoredPaths;
    readonly TimeSpan _debounce;
    readonly bool _includeTempDirectories;

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
    public IReadOnlyDictionary<string, string> ScanErrors { get { lock (_errorsLock) return _scanErrors; } }

    [UsedImplicitly]
    public IReadOnlyCollection<JournalWarning> ScanWarnings { get { lock (_errorsLock) return _scanWarnings; } }

    public RepositoryWatchService(
        IVolumeChangeSource source,
        IReadOnlyCollection<string> trackedRoots,
        IReadOnlyCollection<string> searchRoots,
        TimeSpan? debounce = null,
        bool includeTempDirectories = false,
        IReadOnlyCollection<string>? ignoredPaths = null)
    {
        _source = source;
        _trackedRoots = trackedRoots;
        _searchRoots = searchRoots;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        _includeTempDirectories = includeTempDirectories;
        _ignoredPaths = ignoredPaths ?? [];
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
        {
            diagnostics.DriveFailed += OnDriveFailed;
            diagnostics.JournalWarningDetected += OnJournalWarningDetected;
        }
        var scanProgress = _source as IVolumeScanProgress;
        if (scanProgress is not null) scanProgress.ScanProgress += OnScanProgress;
        try
        {
            await foreach (var changeEvent in RunCoreAsync(ct).ConfigureAwait(false))
                yield return changeEvent;
        }
        finally
        {
            if (diagnostics is not null)
            {
                diagnostics.DriveFailed -= OnDriveFailed;
                diagnostics.JournalWarningDetected -= OnJournalWarningDetected;
            }
            if (scanProgress is not null) scanProgress.ScanProgress -= OnScanProgress;
        }
    }

    async IAsyncEnumerable<RepositoryChangeEvent> RunCoreAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<RepositoryChangeEvent>();
        using var deathSignal = new SourceDeathSignal(_source, reason =>
        {
            Stopped?.Invoke(reason);
            channel.Writer.TryComplete();
        }, ct);

        Dictionary<string, RepositoryChangeFilter> filtersByVolume;
        try
        {
            filtersByVolume = await BuildFiltersByVolumeAsync(deathSignal.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deathSignal.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            yield break;
        }

        Started?.Invoke();
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
            var errors = new Dictionary<string, string>(_scanErrors, StringComparer.OrdinalIgnoreCase) { [drive] = message };
            _scanErrors = errors.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
        ScanErrorsChanged?.Invoke();
    }

    void OnScanProgress(VolumeScanProgress sample) => ScanProgress?.Invoke(sample);

    // One slot per drive: the newer report is the current one.
    void OnJournalWarningDetected(JournalWarning warning)
    {
        lock (_errorsLock)
        {
            _scanWarnings = _scanWarnings
                .Where(existing => !string.Equals(existing.DriveLetter, warning.DriveLetter,
                    StringComparison.OrdinalIgnoreCase))
                .Append(warning)
                .ToFrozenSet();
        }
        ScanWarningsChanged?.Invoke();
    }

    void SetArmResult(VolumeArmResult armResult)
    {
        lock (_errorsLock)
        {
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

        void OnSourceDied(string reason) { _onDied(reason); _ = _cts.CancelAsync(); }
        public void Dispose() { _source.SourceDied -= OnSourceDied; _cts.Cancel(); _cts.Dispose(); }
    }

    async Task<Dictionary<string, RepositoryChangeFilter>> BuildFiltersByVolumeAsync(CancellationToken ct)
    {
        var trackedByVolume = GroupRootsByVolume(_trackedRoots);
        var searchByVolume = GroupRootsByVolume(_searchRoots);
        var ignoredByVolume = GroupRootsByVolume(_ignoredPaths);
        var volumes = trackedByVolume.Keys.Union(searchByVolume.Keys, StringComparer.OrdinalIgnoreCase).ToArray();

        var armResult = await _source.ArmAndCatchUpAsync(volumes, ct).ConfigureAwait(false);
        SetArmResult(armResult);

        return volumes.ToDictionary(
            volume => volume,
            volume => new RepositoryChangeFilter(
                trackedByVolume.GetValueOrDefault(volume) ?? [],
                searchByVolume.GetValueOrDefault(volume) ?? [],
                ignoredByVolume.GetValueOrDefault(volume) ?? [],
                _includeTempDirectories),
            StringComparer.OrdinalIgnoreCase);
    }

    static Dictionary<string, List<string>> GroupRootsByVolume(IReadOnlyCollection<string> roots)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var volume = GetVolume(root);
            if (volume is null) continue;
            if (!result.TryGetValue(volume, out var list))
                result[volume] = list = [];
            list.Add(root);
        }
        return result;
    }

    static string? GetVolume(string path) => WatchPath.Parse(path).Volume;

    async Task PumpAsync(
        IReadOnlyDictionary<string, RepositoryChangeFilter> filtersByVolume,
        ChannelWriter<RepositoryChangeEvent> writer, CancellationToken ct)
    {
        try
        {
            await using var enumerator = _source.WatchAsync(ct).GetAsyncEnumerator(ct);
            await PumpLoopAsync(enumerator, filtersByVolume, new PendingChanges(), writer, ct).ConfigureAwait(false);
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
