using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard;

public partial class RepositoryDiscoveryCoordinator
{
    RepositoryDiscoveryDependencies Dependencies =>
        _dependencies ?? throw new InvalidOperationException("Dependencies are required for discovery.");

    public Task<RepositoryDiscoveryResult> DiscoverAsync(
        DiscoveryRequest request,
        IUpdateHandler? updateHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = DiscoveryScope.Create(request, Dependencies.ClassifyRoot);
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
            return CreateRepositoryListResult(scope, previousList, updateHandler);

        var accumulator = new DiscoveryAccumulator(scope, new List<string>());
        MftIndexSession? session = null;
        try
        {
            if (scope.RequestedNtfsDrives.Count > 0 && !request.NoMft)
            {
                session = await ReusePreparedSessionAsync(scope, prepared).ConfigureAwait(false) ??
                    await dependencies.OpenIndexAsync(
                        scope.RequestedNtfsDrives, CacheMode.ExistingOnly, null, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await RescanDrivesNeedingScanAsync(session, request.Purpose, accumulator, null, cancellationToken).ConfigureAwait(false);
            }
            else if (prepared is not null)
            {
                await prepared.DisposeAsync().ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (session is not null)
            {
                accumulator.RecordDriveStatuses(session.CaptureDriveStatuses());
                AddIndexedCandidates(session.Index, accumulator, cancellationToken);
            }

            if (!request.NoMft)
            {
                accumulator.AddIndexedObservations(scope.SearchRoots, false, DiscoverySource.Snapshot, updateHandler);
                accumulator.AddIndexedObservations(scope.RetainedRoots, true, DiscoverySource.Snapshot, null);
            }

            await CrawlRecursiveRootsAsync(request, accumulator, updateHandler, cancellationToken).ConfigureAwait(false);

            accumulator.AddClassificationErrorObservations();
            var inventory = accumulator.BuildInventory(DetermineRoute(scope), scope.RequestedNtfsDrives);
            await PersistAsync(previousList, inventory, cancellationToken).ConfigureAwait(false);

            var result = new RepositoryDiscoveryResult(inventory, session);
            session = null;
            return result;
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
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
            Route = DetermineRoute(scope),
            Paths = previousList,
            RequestedNtfsDrives = scope.RequestedNtfsDrives,
            Observations = listObservations,
            DriveStatuses = [],
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = scope.WatchExcludedRoots
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
        var roots = accumulator.Scope.RecursiveRoots;
        if (roots.Count > 0)
            StartCrawlProgress(progressHandler, roots.Count);

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

    static async Task<MftIndexSession?> ReusePreparedSessionAsync(DiscoveryScope scope, MftIndexSession? prepared)
    {
        if (prepared is null)
            return null;

        if (!prepared.IsDisposed && !prepared.IsFaulted)
        {
            var available = prepared.Drives
                .Select(value => DriveKey.TryParse(value, out var drive) ? (DriveKey?)drive : null)
                .OfType<DriveKey>().ToHashSet();
            if (scope.RequestedNtfsDrives.All(value =>
                DriveKey.TryParse(value, out var drive) && available.Contains(drive)))
                return prepared;
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
        var drivesNeedingScan = session.CaptureDriveStatuses()
            .Where(driveStatus => NeedsScan(purpose, driveStatus))
            .Select(driveStatus => driveStatus.Drive)
            .ToArray();
        foreach (var drive in drivesNeedingScan)
            progress?.Report(new WatchStartupProgress(WatchStartupPhase.Scanning, drive));

        if (drivesNeedingScan.Length == 0)
            return;

        var results = await session.Index.RescanAsync(drivesNeedingScan.Select(drive => (DriveKey)drive[0]),
            cancellationToken).ConfigureAwait(false);
        foreach (var result in results)
        {
            var drive = result.Drive.ToString();
            if (result.Outcome == DriveOperationOutcome.Succeeded)
                accumulator.RescannedDrives.Add(drive);
            else if (result.Outcome == DriveOperationOutcome.Failed)
                accumulator.Errors[drive] = result.Failure?.Message ?? "The drive rescan failed.";
        }
    }

    void AddIndexedCandidates(FileIndex index, DiscoveryAccumulator accumulator, CancellationToken cancellationToken)
    {
        if (accumulator.ObservedDrives.Count == 0)
            return;

        foreach (var candidate in GitWizardApi.SelectRepositoryCandidates(index, accumulator.Scope, cancellationToken))
        {
            var candidateDrive = FindCandidateDrive(accumulator.Scope, candidate.Path);
            if (candidateDrive is not null && !accumulator.ObservedDrives.Contains(candidateDrive))
                continue;

            if (Dependencies.CandidateExists(candidate.Path, candidate.Kind))
                accumulator.Paths.Add(candidate.Path);
        }
    }

    static string? FindCandidateDrive(DiscoveryScope scope, string candidatePath)
    {
        var candidate = WatchPath.Parse(candidatePath);
        var classification = scope.Classifications.FirstOrDefault(item =>
            item.NtfsDrive is not null && candidate.IsUnderOrEqual(WatchPath.Parse(item.Root)));
        return classification?.NtfsDrive ?? candidate.Volume;
    }

    static DiscoveryRoute DetermineRoute(DiscoveryScope scope)
    {
        if (scope.RequestedNtfsDrives.Count > 0 && scope.RecursiveRoots.Count > 0)
            return DiscoveryRoute.Mixed;
        if (scope.RequestedNtfsDrives.Count > 0)
            return DiscoveryRoute.Indexed;
        return DiscoveryRoute.RecursiveOnly;
    }

    static bool NeedsScan(DiscoveryPurpose purpose, MftDriveStatus drive)
    {
        if (drive.IndexStatus is not { } status || status.State == DriveState.Offline ||
            status.Failure?.Kind == DriveFailureKind.InUse)
            return false;
        if (purpose == DiscoveryPurpose.Rebuild)
            return true;
        if (status.Failure?.Kind is DriveFailureKind.CacheDeclined or DriveFailureKind.CacheIdentityMismatch)
            return true;
        return purpose == DiscoveryPurpose.Watch && status.Watch.CheckpointLoss is { } loss &&
               !IsResolvedByOpeningScan(loss, status);
    }

    // CheckpointLoss is a standing report. An ordinary open that met an unresumable block already
    // cold-scanned it and keeps the DriveOpening report as the explanation; only a cache-only open
    // adopts the unresumable block itself (WarmStartedFromCache), which a watch must replace first.
    static bool IsResolvedByOpeningScan(JournalCheckpointLoss loss, DriveStatus status) =>
        loss.DetectedDuring == JournalCheckpointLossDetection.DriveOpening &&
        status.Block?.Origin == BlockOrigin.ProducedByScan;
}
