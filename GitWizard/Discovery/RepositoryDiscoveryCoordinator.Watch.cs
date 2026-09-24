using GitWizard.Watch;

namespace GitWizard;

public partial class RepositoryDiscoveryCoordinator
{
    // Takes ownership of a non-null prepared owner on every platform: callers hand it over
    // without knowing which platform branch runs, so only the Windows index path may adopt it
    // and every other branch disposes it.
    public Task<WatchStartupResult> PrepareWatchAsync(
        DiscoveryRequest request,
        MftIndexSession? prepared,
        CancellationToken cancellationToken) =>
        PrepareWatchAsync(request, prepared, null, cancellationToken);

    public async Task<WatchStartupResult> PrepareWatchAsync(
        DiscoveryRequest request,
        MftIndexSession? prepared,
        IProgress<WatchStartupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var dependencies = Dependencies;
        if (!dependencies.IsWindows && prepared is not null)
            await prepared.DisposeAsync().ConfigureAwait(false);

        ArgumentNullException.ThrowIfNull(request);
        var scope = DiscoveryScope.Create(request, dependencies.ClassifyRoot);

        if (dependencies.IsWindows)
            return await PrepareWindowsWatchAsync(request, scope, prepared, progress, cancellationToken).ConfigureAwait(false);

        if (dependencies.IsLinux)
            return await PrepareLinuxWatchAsync(request, scope, progress, cancellationToken).ConfigureAwait(false);

        throw new PlatformNotSupportedException("Watch mode currently requires Windows or Linux.");
    }

    async Task<WatchStartupResult> PrepareWindowsWatchAsync(
        DiscoveryRequest request,
        DiscoveryScope scope,
        MftIndexSession? prepared,
        IProgress<WatchStartupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var dependencies = Dependencies;
        var previousList = await dependencies.ReadListAsync(cancellationToken).ConfigureAwait(false) ?? [];
        var accumulator = new DiscoveryAccumulator(scope, new HashSet<string>(WatchPath.StringComparer));
        var (source, armResult) = await ArmWindowsWatchSourceAsync(scope, prepared, accumulator, progress, cancellationToken).ConfigureAwait(false);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (source?.Session is { } session)
                accumulator.RecordDriveStatuses(session.CaptureDriveStatuses());

            progress?.Report(new WatchStartupProgress(WatchStartupPhase.Discovering));
            if (source?.Index is { } index && !request.NoMft)
                AddIndexedCandidates(index, accumulator, cancellationToken);

            await CrawlRecursiveRootsAsync(request, accumulator, null, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<VolumeChangeBatch> startupBatches = [];
            if (source is not null)
            {
                progress?.Report(new WatchStartupProgress(WatchStartupPhase.Reconciling));
                startupBatches = ReconcileBufferedBatches(source, accumulator, cancellationToken);
            }

            accumulator.AddIndexedObservations(scope.SearchRoots, false, DiscoverySource.Replay, null);
            accumulator.AddIndexedObservations(scope.RetainedRoots, true, DiscoverySource.Replay, null);
            accumulator.AddClassificationErrorObservations();
            var inventory = accumulator.BuildInventory(DetermineRoute(scope), scope.RequestedNtfsDrives);
            await PersistAsync(previousList, inventory, cancellationToken).ConfigureAwait(false);

            if (inventory.Outcome is DiscoveryOutcome.CompleteEmpty or DiscoveryOutcome.Unusable && source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
                source = null;
            }

            var watchVolumes = accumulator.ObservedDrives.Count > 0
                ? scope.RequestedNtfsDrives.Where(accumulator.ObservedDrives.Contains).ToArray()
                : scope.RequestedNtfsDrives;
            var result = new WatchStartupResult(inventory, armResult, source, watchVolumes, scope, startupBatches);
            source = null;
            return result;
        }
        finally
        {
            if (source is not null)
                await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    async Task<(IndexVolumeChangeSource? Source, VolumeArmResult ArmResult)> ArmWindowsWatchSourceAsync(
        DiscoveryScope scope,
        MftIndexSession? prepared,
        DiscoveryAccumulator accumulator,
        IProgress<WatchStartupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (scope.RequestedNtfsDrives.Count == 0)
        {
            if (prepared is not null)
                await prepared.DisposeAsync().ConfigureAwait(false);
            return (null, new VolumeArmResult(accumulator.Errors, []));
        }

        progress?.Report(new WatchStartupProgress(WatchStartupPhase.Opening));
        var (source, detachBroker) = await OpenPreparedWatchSourceAsync(scope, prepared, accumulator, progress, cancellationToken).ConfigureAwait(false);
        var armResult = new VolumeArmResult(accumulator.Errors, []);
        if (source is null)
            return (null, armResult);

        void ForwardReadiness(VolumeReadinessSnapshot snapshot) =>
            progress?.Report(new WatchStartupProgress(WatchStartupPhase.CatchingUp, null, snapshot));
        source.ReadinessChanged += ForwardReadiness;
        try
        {
            progress?.Report(new WatchStartupProgress(WatchStartupPhase.CatchingUp));
            source.StartStartupBuffering();
            armResult = await source.ArmAndCatchUpAsync(scope.RequestedNtfsDrives, cancellationToken).ConfigureAwait(false);
            foreach (var (drive, message) in armResult.Errors)
                accumulator.Errors[drive] = message;
            return (source, armResult);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            source.ReadinessChanged -= ForwardReadiness;
            detachBroker?.Invoke();
        }
    }

    async Task<(IndexVolumeChangeSource? Source, Action? DetachBroker)> OpenPreparedWatchSourceAsync(
        DiscoveryScope scope,
        MftIndexSession? prepared,
        DiscoveryAccumulator accumulator,
        IProgress<WatchStartupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var session = await ReusePreparedSessionAsync(scope, prepared).ConfigureAwait(false);
        if (session is null)
        {
            try
            {
                session = await Dependencies.OpenIndexAsync(
                    scope.RequestedNtfsDrives, MftIndexOpenIntent.Snapshot, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                              !cancellationToken.IsCancellationRequested)
            {
                foreach (var drive in scope.RequestedNtfsDrives)
                    accumulator.Errors[drive] = exception.Message;
                return (null, null);
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            void OnConnecting() => progress?.Report(new WatchStartupProgress(WatchStartupPhase.RequestingElevation));
            void OnConnected() => progress?.Report(new WatchStartupProgress(WatchStartupPhase.BrokerConnected));
            var connection = session.BrokerConnection;
            Action? detach = null;
            if (connection is not null)
            {
                connection.Connecting += OnConnecting;
                connection.Connected += OnConnected;
                detach = () =>
                {
                    connection.Connecting -= OnConnecting;
                    connection.Connected -= OnConnected;
                };
            }
            try
            {
                await RescanDrivesNeedingScanAsync(session, DiscoveryPurpose.Watch, accumulator, progress, cancellationToken).ConfigureAwait(false);
                var source = IndexVolumeChangeSource.CreatePrepared(session);
                session = null;
                var sourceDetach = detach;
                detach = null;
                return (source, sourceDetach);
            }
            finally
            {
                detach?.Invoke();
            }
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Drains the source's startup buffer once: every buffered entry reconciles membership
    // (existence re-checks and moved-in candidates), and the batches are returned so the
    // watch service can deliver them through its normal ordered event path once its filters
    // are built from the reconciled inventory.
    IReadOnlyList<VolumeChangeBatch> ReconcileBufferedBatches(
        IndexVolumeChangeSource source, DiscoveryAccumulator accumulator, CancellationToken cancellationToken)
    {
        var batches = source.CompleteStartupBuffer();
        var movedRoots = new List<WatchPath>();
        foreach (var batch in batches)
        {
            foreach (var entry in batch.Entries)
            {
                foreach (var target in GetReconciliationTargets(entry.FullPath))
                    ReconcileCandidateInvalidation(target, accumulator);

                if (entry.Kind != VolumeEntryKind.MovedIn)
                    continue;

                var movedRoot = WatchPath.Parse(entry.FullPath);
                if (!movedRoots.Any(movedRoot.IsUnderOrEqual))
                    movedRoots.Add(movedRoot);
            }
        }

        if (movedRoots.Count > 0 && source.Index is { } index)
        {
            // One candidate selection covers every moved-in subtree; re-enumerating the full
            // index per entry is quadratic when the replay holds O(N) renames.
            foreach (var candidate in GitWizardApi.SelectRepositoryCandidates(index, accumulator.Scope, cancellationToken))
            {
                var candidatePath = WatchPath.Parse(candidate.Path);
                if (movedRoots.Any(candidatePath.IsUnderOrEqual))
                    ReconcileCandidateInvalidation(candidate.Path, accumulator);
            }
        }

        return batches;
    }

    static HashSet<string> GetReconciliationTargets(string entryPath)
    {
        var targets = new HashSet<string>(WatchPath.StringComparer);

        if (WatchPath.Parse(entryPath).TryGetGitDirectoryParent(out var parent))
            targets.Add(parent.Original);

        targets.Add(entryPath);
        var directory = Path.GetDirectoryName(entryPath);
        if (!string.IsNullOrEmpty(directory))
            targets.Add(directory);

        return targets;
    }

    void ReconcileCandidateInvalidation(string candidateRoot, DiscoveryAccumulator accumulator)
    {
        var dependencies = Dependencies;
        var exists = dependencies.CandidateExists(candidateRoot, RepositoryCandidateKind.Ordinary) ||
                     dependencies.CandidateExists(candidateRoot, RepositoryCandidateKind.GitFile) ||
                     dependencies.CandidateExists(candidateRoot, RepositoryCandidateKind.Bare);

        if (exists && IsAllowedReplayCandidate(accumulator.Scope, candidateRoot))
        {
            accumulator.Paths.Add(candidateRoot);
            return;
        }

        accumulator.Paths.Remove(candidateRoot);
    }

    // Applies the same scope policy GitWizardApi.SelectRepositoryCandidates applies to indexed
    // candidates: a replay candidate enters the inventory only when a search root claims it
    // under the configured ignored-, hidden-, and temporary-directory rules, or when it is an
    // exact retained root outside every search root. Internal for direct policy tests.
    internal static bool IsAllowedReplayCandidate(DiscoveryScope scope, string candidateRoot)
    {
        var candidate = WatchPath.Parse(candidateRoot);
        if (scope.IgnoredRoots.Any(root => candidate.IsUnderOrEqual(WatchPath.Parse(root))))
            return false;

        var configuration = scope.Request.Configuration;
        var underSearchRoot = false;
        foreach (var searchRoot in scope.SearchRoots)
        {
            var root = WatchPath.Parse(searchRoot);
            if (!candidate.IsUnderOrEqual(root))
                continue;

            underSearchRoot = true;
            if (configuration.IncludeTempDirectories != true &&
                !TempDirectories.IsTempPath(root.Original) &&
                TempDirectories.IsTempPath(candidate.Original))
                continue;
            if (configuration.SkipHiddenDirectories != false &&
                candidate.Comparison.Length > root.Comparison.Length &&
                candidate.Comparison[root.Comparison.Length..].Split('/').Any(segment => segment.StartsWith('.')))
                continue;

            return true;
        }

        if (underSearchRoot)
            return false;

        return scope.RetainedRoots.Any(root => candidate.IsEqualTo(WatchPath.Parse(root)));
    }
}
