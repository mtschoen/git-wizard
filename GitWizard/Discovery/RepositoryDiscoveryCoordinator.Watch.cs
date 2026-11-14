using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard.Discovery;

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
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
        }

        ArgumentNullException.ThrowIfNull(request);
        var scope = DiscoveryScope.Create(request);

        if (dependencies.IsWindows)
        {
            return await PrepareWindowsWatchAsync(scope, prepared, progress, cancellationToken).ConfigureAwait(false);
        }

        if (dependencies.IsLinux)
        {
            return await PrepareLinuxWatchAsync(request, scope, progress, cancellationToken).ConfigureAwait(false);
        }

        throw new PlatformNotSupportedException("Watch mode currently requires Windows or Linux.");
    }

    async Task<WatchStartupResult> PrepareWindowsWatchAsync(
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
            {
                accumulator.RecordDriveStatuses(session.CaptureRootStatuses());
            }

            progress?.Report(new WatchStartupProgress(WatchStartupPhase.Discovering));
            if (source?.IndexSet is { } index)
            {
                AddIndexedCandidates(index, accumulator, cancellationToken);
            }


            IReadOnlyList<VolumeChangeBatch> startupBatches = [];
            if (source is not null)
            {
                progress?.Report(new WatchStartupProgress(WatchStartupPhase.Reconciling));
                startupBatches = ReconcileBufferedBatches(source, accumulator, progress, cancellationToken);
            }

            accumulator.AddIndexedObservations(false, DiscoverySource.Replay, null);
            accumulator.AddIndexedObservations(true, DiscoverySource.Replay, null);
            accumulator.AddExpansionErrorObservations();
            var inventory = accumulator.BuildInventory(DetermineRoute(source?.Session), WatchVolumes(source?.Session));
            await PersistAsync(previousList, inventory, cancellationToken).ConfigureAwait(false);

            if (inventory.Outcome is DiscoveryOutcome.CompleteEmpty or DiscoveryOutcome.Unusable && source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
                source = null;
            }

            var watchVolumes = WatchVolumes(source?.Session);
            var result = new WatchStartupResult(inventory, armResult, source, watchVolumes, scope, startupBatches);
            source = null;
            return result;
        }
        finally
        {
            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    async Task<(IndexVolumeChangeSource? Source, VolumeArmResult ArmResult)> ArmWindowsWatchSourceAsync(
        DiscoveryScope scope,
        MftIndexSession? prepared,
        DiscoveryAccumulator accumulator,
        IProgress<WatchStartupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (scope.IndexRoots.Count == 0)
        {
            if (prepared is not null)
            {
                await prepared.DisposeAsync().ConfigureAwait(false);
            }

            return (null, new VolumeArmResult(accumulator.Errors, []));
        }

        progress?.Report(new WatchStartupProgress(WatchStartupPhase.Opening));
        var (source, detachBroker) = await OpenPreparedWatchSourceAsync(scope, prepared, accumulator, progress, cancellationToken).ConfigureAwait(false);
        var armResult = new VolumeArmResult(accumulator.Errors, []);
        if (source is null)
        {
            return (null, armResult);
        }

        void ForwardReadiness(VolumeReadinessSnapshot snapshot) =>
            progress?.Report(new WatchStartupProgress(WatchStartupPhase.CatchingUp, null, snapshot));
        source.ReadinessChanged += ForwardReadiness;
        try
        {
            progress?.Report(new WatchStartupProgress(WatchStartupPhase.CatchingUp));
            source.StartStartupBuffering();
            armResult = await source.ArmAndCatchUpAsync(scope.IndexRoots, cancellationToken).ConfigureAwait(false);
            foreach (var (drive, message) in armResult.Errors)
            {
                accumulator.Errors[drive] = message;
                if (VolumeKey.TryParse(drive, out var failedVolume) && source.Session is { } owner)
                {
                    foreach (var index in owner.Indices.Where(index => owner.TryGetVolume(index.Status.Root, out var volume) &&
                                 volume == failedVolume))
                    {
                        accumulator.Errors[index.Status.Root.ToString()] = message;
                    }
                }
            }

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
        var session = await ReusePreparedSessionAsync(scope, prepared, requireExactRoots: true).ConfigureAwait(false);
        if (session is null)
        {
            try
            {
                session = await Dependencies.OpenAsync(
                    scope, scope.Request.NoMft ? CacheMode.Disabled : CacheMode.ExistingOnly,
                    null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                              !cancellationToken.IsCancellationRequested)
            {
                foreach (var drive in scope.IndexRoots)
                {
                    accumulator.Errors[drive] = exception.Message;
                }

                return (null, null);
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            void OnConnecting() => progress?.Report(new WatchStartupProgress(WatchStartupPhase.RequestingElevation));
            void OnConnected() => progress?.Report(new WatchStartupProgress(WatchStartupPhase.BrokerConnected));
            var brokerSession = session.BrokerSession;
            Action? detach = null;
            if (brokerSession is not null)
            {
                brokerSession.Connecting += OnConnecting;
                brokerSession.Connected += OnConnected;
                detach = () =>
                {
                    brokerSession.Connecting -= OnConnecting;
                    brokerSession.Connected -= OnConnected;
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
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Drains the source's startup buffer once: every buffered entry reconciles membership
    // (existence re-checks and moved-in candidates), and the batches are returned so the
    // watch service can deliver them through its normal ordered event path once its filters
    // are built from the reconciled inventory.
    IReadOnlyList<VolumeChangeBatch> ReconcileBufferedBatches(
        IndexVolumeChangeSource source, DiscoveryAccumulator accumulator,
        IProgress<WatchStartupProgress>? progress, CancellationToken cancellationToken)
    {
        var batches = source.CompleteStartupBuffer();
        ReconcileBatches(batches, source.IndexSet, accumulator, progress, cancellationToken);
        return batches;
    }

    // Collects every entry's reconciliation targets first and probes each distinct target once:
    // a probe reads the current disk state, so the last probe of a repeated target is the only
    // one that decides membership, and a replay of N entries touches far fewer than N paths.
    // Targets are probed in first-seen order. Cancellation is observed per batch and per target,
    // and progress counts probed targets against the distinct total.
    internal void ReconcileBatches(
        IReadOnlyList<VolumeChangeBatch> batches, FileIndexSet? index, DiscoveryAccumulator accumulator,
        IProgress<WatchStartupProgress>? progress, CancellationToken cancellationToken)
    {
        var movedRoots = new List<WatchPath>();
        var seenTargets = new HashSet<string>(WatchPath.StringComparer);
        var targets = new List<string>();
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in batch.Entries)
            {
                foreach (var target in GetReconciliationTargets(entry.FullPath, accumulator.Paths))
                {
                    if (seenTargets.Add(target))
                    {
                        targets.Add(target);
                    }
                }

                if (entry.Kind != VolumeEntryKind.MovedIn)
                {
                    continue;
                }

                var movedRoot = WatchPath.Parse(entry.FullPath);
                if (!movedRoots.Any(movedRoot.IsUnderOrEqual))
                {
                    movedRoots.Add(movedRoot);
                }
            }
        }

        var interval = Dependencies.ReconcileProgressInterval;
        for (var probed = 0; probed < targets.Count; probed++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReconcileCandidateInvalidation(targets[probed], accumulator);
            if (progress is not null && (probed + 1) % interval == 0)
            {
                progress.Report(new WatchStartupProgress(
                    WatchStartupPhase.Reconciling, Completed: probed + 1, Total: targets.Count));
            }
        }

        if (movedRoots.Count == 0 || index is null)
        {
            return;
        }

        // One candidate selection covers every moved-in subtree; re-enumerating the full
        // index per entry is quadratic when the replay holds O(N) renames.
        foreach (var candidate in GitWizardApi.SelectRepositoryCandidates(index, accumulator.Scope, cancellationToken))
        {
            var candidatePath = WatchPath.Parse(candidate.Path);
            if (movedRoots.Any(candidatePath.IsUnderOrEqual))
            {
                ReconcileCandidateInvalidation(candidate.Path, accumulator);
            }
        }
    }

    // Prototype of remedy E: only a path that can be a repository root is a target: the parent of a
    // .git entry, and the nearest already-known repository root at or above the entry.
    static HashSet<string> GetReconciliationTargets(string entryPath, ICollection<string> knownRoots)
    {
        var targets = new HashSet<string>(WatchPath.StringComparer);
        var path = WatchPath.Parse(entryPath);
        if (path.TryGetGitDirectoryParent(out var parent))
        {
            targets.Add(parent.Original);
        }

        var directory = entryPath;
        while (!string.IsNullOrEmpty(directory))
        {
            if (knownRoots.Contains(directory))
            {
                targets.Add(directory);
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return targets;
    }

    void ReconcileCandidateInvalidation(string candidateRoot, DiscoveryAccumulator accumulator)
    {
        var dependencies = Dependencies;
        var exists = dependencies.CandidateExists(candidateRoot, RepositoryCandidateKind.Ordinary) ||
                     dependencies.CandidateExists(candidateRoot, RepositoryCandidateKind.GitFile) ||
                     dependencies.CandidateExists(candidateRoot, RepositoryCandidateKind.Bare);

        if (exists && CandidateAdmissionPolicy.AdmitsInventoryCandidate(accumulator.CandidateScope, candidateRoot))
        {
            accumulator.Paths.Add(candidateRoot);
            return;
        }

        accumulator.Paths.Remove(candidateRoot);
    }
}
