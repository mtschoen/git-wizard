using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard.Discovery;

public partial class RepositoryDiscoveryCoordinator
{
    RepositoryDiscoveryDependencies Dependencies =>
        _dependencies ?? throw new InvalidOperationException("Dependencies are required for discovery.");

    internal IReadOnlyList<string> AdditionalCloneRoots => Dependencies.AdditionalCloneRoots();

    public Task<RepositoryDiscoveryResult> DiscoverAsync(
        DiscoveryRequest request,
        IUpdateHandler? updateHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = DiscoveryScope.Create(request);
        return DiscoverScopeAsync(request, scope, null, updateHandler, cancellationToken);
    }

    internal async Task<RepositoryDiscoveryResult> DiscoverScopeAsync(
        DiscoveryRequest request,
        DiscoveryScope scope,
        MftIndexSession? prepared,
        IUpdateHandler? updateHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scope);
        var dependencies = Dependencies;

        var previousList = await dependencies.ReadListAsync(cancellationToken).ConfigureAwait(false) ?? [];
        if (request.Purpose == DiscoveryPurpose.Ordinary && request.AllowRepositoryList && previousList.Length > 0)
        {
            return CreateRepositoryListResult(scope, previousList, updateHandler);
        }

        var accumulator = new DiscoveryAccumulator(scope, new List<string>());
        MftIndexSession? session = null;
        try
        {
            if (Dependencies.IsWindows && scope.IndexRoots.Count > 0)
            {
                StartCrawlProgress(updateHandler, scope.IndexRoots.Count);
                session = await ReusePreparedSessionAsync(scope, prepared).ConfigureAwait(false) ??
                    await dependencies.OpenAsync(
                        scope, request.NoMft ? CacheMode.Disabled : CacheMode.ExistingOnly,
                        null, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await RescanDrivesNeedingScanAsync(session, request.Purpose, accumulator, null, cancellationToken).ConfigureAwait(false);
                UpdateCrawlProgress(updateHandler, scope.IndexRoots.Count);
            }
            else if (prepared is not null)
            {
                await prepared.DisposeAsync().ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (session is not null)
            {
                accumulator.RecordDriveStatuses(session.CaptureRootStatuses());
                var observed = AddIndexedCandidates(session.IndexSet, accumulator, cancellationToken);
                if (observed is not null)
                {
                    StartRepositorySizePass(observed, accumulator, cancellationToken);
                }
            }

            if (session is not null)
            {
                accumulator.AddIndexedObservations(false, DiscoverySource.Snapshot, updateHandler);
                accumulator.AddIndexedObservations(true, DiscoverySource.Snapshot, null);
            }

            if (!Dependencies.IsWindows)
            {
                await CrawlRecursiveRootsAsync(request, accumulator, updateHandler, cancellationToken).ConfigureAwait(false);
            }

            accumulator.AddExpansionErrorObservations();
            var inventory = accumulator.BuildInventory(DetermineRoute(session), WatchVolumes(session));
            await PersistAsync(previousList, inventory, cancellationToken).ConfigureAwait(false);

            var result = new RepositoryDiscoveryResult(inventory,
                new RepositoryDiscoverySession(session, accumulator.RepositorySizes, accumulator.RepositorySizeCancellation));
            session = null;
            return result;
        }
        finally
        {
            if (session is not null)
            {
                await new RepositoryDiscoverySession(session, accumulator.RepositorySizes,
                    accumulator.RepositorySizeCancellation).DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    static RepositoryDiscoveryResult CreateRepositoryListResult(
        DiscoveryScope scope, string[] previousList, IUpdateHandler? updateHandler)
    {
        var listObservations = scope.SearchRoots.Select(root =>
            new RootObservation(root, false, true, DiscoverySource.RepositoryList, null, null)).ToArray();

        var listInventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.CompleteNonempty,
            Route = DetermineRoute(null),
            Paths = previousList,
            RequestedNtfsDrives = [],
            Observations = listObservations,
            RootStatuses = [],
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };

        updateHandler?.SendUpdateMessage($"Loaded {previousList.Length} repositories from repository list.");
        return new RepositoryDiscoveryResult(listInventory);
    }

    async Task CrawlRecursiveRootsAsync(
        DiscoveryRequest request,
        DiscoveryAccumulator accumulator,
        IUpdateHandler? progressHandler,
        CancellationToken cancellationToken)
    {
        var roots = accumulator.Scope.SearchRoots;
        if (roots.Count > 0)
        {
            StartCrawlProgress(progressHandler, roots.Count);
        }

        var completed = 0;
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var crawlResult = await Dependencies.CrawlRootAsync(request, root, cancellationToken).ConfigureAwait(false);
            accumulator.AddCrawlResult(root, crawlResult, DiscoverySource.Recursive);
            UpdateCrawlProgress(progressHandler, ++completed);
        }
    }

    static void StartCrawlProgress(IUpdateHandler? progressHandler, int total)
    {
        try
        {
            progressHandler?.StartProgress("Getting GitWizardRepository Paths", total);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GitWizardLog.LogException(exception, "Exception thrown by discovery StartProgress callback.");
        }
    }

    static void UpdateCrawlProgress(IUpdateHandler? progressHandler, int completed)
    {
        try
        {
            progressHandler?.UpdateProgress(completed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GitWizardLog.LogException(exception, "Exception thrown by discovery UpdateProgress callback.");
        }
    }

    static async Task<MftIndexSession?> ReusePreparedSessionAsync(DiscoveryScope scope, MftIndexSession? prepared,
        bool requireExactRoots = false)
    {
        if (prepared is null)
        {
            return null;
        }

        if (!prepared.IsDisposed && !prepared.IsFaulted)
        {
            var available = RootKeyProjection.ParseRootKeys(prepared.Drives).ToHashSet();
            var requested = RootKeyProjection.ParseRootKeys(scope.IndexRoots);
            if (requested.Length == scope.IndexRoots.Count && requested.All(available.Contains) &&
                (!requireExactRoots || available.SetEquals(requested)))
            {
                return prepared;
            }
        }

        await prepared.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    static async Task RescanDrivesNeedingScanAsync(
        MftIndexSession session,
        DiscoveryPurpose purpose,
        DiscoveryAccumulator accumulator,
        IProgress<WatchStartupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var drivesNeedingScan = session.CaptureRootStatuses()
            .Where(driveStatus => NeedsScan(purpose, driveStatus))
            .Select(driveStatus => driveStatus.Drive)
            .ToArray();
        foreach (var drive in drivesNeedingScan)
        {
            progress?.Report(new WatchStartupProgress(WatchStartupPhase.Scanning, drive));
        }

        if (drivesNeedingScan.Length == 0)
        {
            return;
        }

        var rootsNeedingScan = RootKeyProjection.ParseRootKeys(drivesNeedingScan).ToHashSet();
        var results = await Task.WhenAll(session.Indices
            .Where(index => rootsNeedingScan.Contains(index.Status.Root))
            .Select(index => index.RescanAsync(cancellationToken))).ConfigureAwait(false);
        foreach (var result in results)
        {
            var drive = result.Root.ToString();
            switch (result.Outcome)
            {
                case RootOperationOutcome.Succeeded:
                    accumulator.RescannedDrives.Add(drive);
                    break;
                case RootOperationOutcome.Failed:
                    accumulator.Errors[drive] = result.Failure?.Message ?? "The drive rescan failed.";
                    break;
                case RootOperationOutcome.NotApplicable:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(session), result.Outcome, "Unknown rescan outcome.");
            }
        }
    }

    FileIndexSet? AddIndexedCandidates(FileIndexSet index, DiscoveryAccumulator accumulator, CancellationToken cancellationToken)
    {
        if (accumulator.ObservedDrives.Count == 0)
        {
            return null;
        }

        var observed = new FileIndexSet([.. index.Indices.Where(member =>
            accumulator.ObservedDrives.Contains(member.Status.Root.ToString()))]);
        accumulator.CandidateScope = ResolvedDiscoveryScope.Create(observed, accumulator.Scope);
        foreach (var candidate in GitWizardApi.SelectRepositoryCandidates(observed, accumulator.CandidateScope, cancellationToken))
        {
            if (Dependencies.CandidateExists(candidate.Path, candidate.Kind))
            {
                accumulator.Paths.Add(candidate.Path);
            }
        }

        return observed;
    }

    void StartRepositorySizePass(FileIndexSet observed, DiscoveryAccumulator accumulator, CancellationToken cancellationToken)
    {
        var sizeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        accumulator.RepositorySizeCancellation = sizeCancellation;
        accumulator.RepositorySizes = Task.Run(
            () => Dependencies.ComputeRepositorySizes(observed, [.. accumulator.Paths], sizeCancellation.Token),
            CancellationToken.None);
    }

    static IReadOnlyList<string> WatchVolumes(MftIndexSession? session) => session?.ObservedMftVolumes ?? [];

    static DiscoveryRoute DetermineRoute(MftIndexSession? session)
    {
        if (session is null || session.Indices.Count == 0)
        {
            return DiscoveryRoute.RecursiveOnly;
        }

        var mftCount = session.Indices.Count(index => index.Status.Producer == ProducerKind.Mft);
        if (mftCount == 0)
        {
            return DiscoveryRoute.RecursiveOnly;
        }

        return mftCount == session.Indices.Count ? DiscoveryRoute.Indexed : DiscoveryRoute.Mixed;
    }

    static bool NeedsScan(DiscoveryPurpose purpose, MftRootStatus drive)
    {
        if (drive.IndexStatus is not { } status || status.State == RootState.Offline ||
            status.Failure?.Kind == RootFailureKind.InUse)
        {
            return false;
        }

        if (purpose == DiscoveryPurpose.Rebuild)
        {
            return true;
        }

        if (status.Failure?.Kind is RootFailureKind.CacheDeclined or RootFailureKind.CacheIdentityMismatch)
        {
            return true;
        }

        return purpose == DiscoveryPurpose.Watch && status.Watch.CheckpointLoss is { } loss &&
               !IsResolvedByOpeningScan(loss, status);
    }

    // CheckpointLoss is a standing report. An ordinary open that met an unresumable block already
    // cold-scanned it and keeps the RootOpening report as the explanation; only a cache-only open
    // adopts the unresumable block itself (WarmStartedFromCache), which a watch must replace first.
    static bool IsResolvedByOpeningScan(JournalCheckpointLoss loss, RootStatus status) =>
        loss.DetectedDuring == JournalCheckpointLossDetection.RootOpening &&
        status.Block?.Origin == BlockOrigin.ProducedByScan;
}
