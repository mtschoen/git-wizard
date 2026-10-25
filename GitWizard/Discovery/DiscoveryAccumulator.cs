using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard.Discovery;

/// <summary>
/// Collects the paths, errors, observations, and drive outcomes of one discovery pass and turns
/// them into the pass's <see cref="DiscoveryInventory" />.
/// </summary>
sealed class DiscoveryAccumulator
{
    public DiscoveryAccumulator(DiscoveryScope scope, ICollection<string> paths)
    {
        Scope = scope;
        Paths = paths;
        Errors = new Dictionary<string, string>(scope.ClassificationErrors, StringComparer.OrdinalIgnoreCase);
    }

    public DiscoveryScope Scope { get; }
    public ICollection<string> Paths { get; }
    public Dictionary<string, string> Errors { get; }
    public List<RootObservation> Observations { get; } = [];
    public HashSet<string> RescannedDrives { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ObservedDrives { get; } = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyList<MftDriveStatus> FinalStatuses { get; set; } = [];

    public void RecordDriveStatuses(IReadOnlyList<MftDriveStatus> statuses)
    {
        FinalStatuses = statuses;
        foreach (var status in statuses)
        {
            if (status.IdentityFailure is not null)
            {
                Errors[status.Drive] = status.IdentityFailure;
            }
            else if (status.IndexStatus is not { } indexStatus)
            {
                Errors[status.Drive] = $"Drive {status.Drive} has no index status.";
            }
            else if (IsFailed(indexStatus))
            {
                Errors[status.Drive] = GetIndexStatusFailureMessage(indexStatus);
            }
            else
            {
                ObservedDrives.Add(status.Drive);
            }
        }
    }

    public void AddIndexedObservations(
        IEnumerable<string> roots, bool exactRoot, DiscoverySource unrescannedSource, IUpdateHandler? snapshotUpdates)
    {
        foreach (var root in roots)
        {
            var classification = Scope.Classifications.FirstOrDefault(item =>
                string.Equals(item.Root, root, StringComparison.OrdinalIgnoreCase));
            if (classification is not { FileSystem: RootFileSystem.Ntfs, NtfsDrive: { } drive })
            {
                continue;
            }

            var driveStatus = FinalStatuses.FirstOrDefault(item =>
                string.Equals(item.Drive, drive, StringComparison.OrdinalIgnoreCase));
            var isSuccess = ObservedDrives.Contains(drive);
            var source = RescannedDrives.Contains(drive) ? DiscoverySource.Scan : unrescannedSource;
            var timestamp = isSuccess ? driveStatus?.IndexStatus?.Block?.ScanTimestamp : null;
            var error = isSuccess ? null : Errors.GetValueOrDefault(drive) ?? classification.Error;

            if (isSuccess && source == DiscoverySource.Snapshot && timestamp.HasValue)
            {
                snapshotUpdates?.SendUpdateMessage($"Using index snapshot for {drive}: (scan timestamp {timestamp.Value:O}; may be stale)");
            }

            Observations.Add(new RootObservation(root, exactRoot, isSuccess, source, timestamp, error));
        }
    }

    public void AddCrawlResult(string root, RecursiveDiscovery crawlResult, DiscoverySource source)
    {
        foreach (var path in crawlResult.Paths)
        {
            Paths.Add(path);
        }

        foreach (var (key, message) in crawlResult.Errors)
        {
            Errors[key] = message;
        }

        var isComplete = crawlResult.Errors.Count == 0 && crawlResult.FailedSubtrees.Count == 0;
        var crawlError = isComplete ? null : crawlResult.Errors.GetValueOrDefault(root);
        Observations.Add(new RootObservation(root, false, isComplete, source, null, crawlError));

        foreach (var failedSubtree in crawlResult.FailedSubtrees)
        {
            var subtreeError = crawlResult.Errors.GetValueOrDefault(failedSubtree, "Access denied");
            Observations.Add(new RootObservation(failedSubtree, false, false, source, null, subtreeError));
        }
    }

    public void AddClassificationErrorObservations()
    {
        foreach (var root in Scope.SearchRoots.Concat(Scope.RetainedRoots))
        {
            if (!Scope.ClassificationErrors.TryGetValue(root, out var classificationError))
            {
                continue;
            }

            if (Observations.Any(item => string.Equals(item.Root, root, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var isExact = Scope.RetainedRoots.Contains(root);
            Observations.Add(new RootObservation(root, isExact, false, DiscoverySource.Recursive, null, classificationError));
        }
    }

    public DiscoveryInventory BuildInventory(DiscoveryRoute route, IReadOnlyList<string> requestedNtfsDrives)
    {
        var finalPaths = Paths.Distinct(WatchPath.StringComparer).ToArray();
        return new DiscoveryInventory
        {
            Outcome = DetermineOutcome(finalPaths.Length),
            Route = route,
            Paths = finalPaths,
            RequestedNtfsDrives = requestedNtfsDrives,
            Observations = Observations,
            DriveStatuses = FinalStatuses,
            Errors = Errors,
            WatchExcludedRoots = Scope.WatchExcludedRoots
        };
    }

    DiscoveryOutcome DetermineOutcome(int pathCount)
    {
        if (Scope.SearchRoots.Count == 0 && Scope.RetainedRoots.Count == 0)
        {
            Errors["*"] = "No search roots configured.";
            return DiscoveryOutcome.Unusable;
        }

        var hasFailures = Errors.Count > 0 || Observations.Any(item => !item.Complete);
        if (!hasFailures)
        {
            return pathCount == 0 ? DiscoveryOutcome.CompleteEmpty : DiscoveryOutcome.CompleteNonempty;
        }

        return Observations.Any(item => item.Complete) ? DiscoveryOutcome.Partial : DiscoveryOutcome.Unusable;
    }

    static bool IsFailed(DriveStatus indexStatus) =>
        indexStatus.State is DriveState.Failed or DriveState.Offline ||
        indexStatus.Failure is not null;

    static string GetIndexStatusFailureMessage(DriveStatus indexStatus)
    {
        return indexStatus.Failure?.Message ?? $"Drive is {indexStatus.State}.";
    }
}
