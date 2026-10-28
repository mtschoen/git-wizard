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
        CandidateScope = scope;
        Paths = paths;
        Errors = new Dictionary<string, string>(scope.ExpansionErrors, StringComparer.OrdinalIgnoreCase);
    }

    public DiscoveryScope Scope { get; }
    public DiscoveryScope CandidateScope { get; set; }
    public ICollection<string> Paths { get; }
    public Dictionary<string, string> Errors { get; }
    public List<RootObservation> Observations { get; } = [];
    public HashSet<string> RescannedDrives { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ObservedDrives { get; } = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyList<MftRootStatus> FinalStatuses { get; set; } = [];

    public void RecordDriveStatuses(IReadOnlyList<MftRootStatus> statuses)
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
            else if (!Errors.ContainsKey(status.Drive))
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
            var indexedRoot = Scope.IndexRoots.Contains(root, WatchPath.StringComparer) ? root :
                Scope.SearchRoots.FirstOrDefault(search => WatchPath.Parse(root).IsUnderOrEqual(WatchPath.Parse(search))) ?? root;
            var key = ToStatusKey(indexedRoot);
            var rootStatus = FinalStatuses.FirstOrDefault(item =>
                string.Equals(item.Drive, key, StringComparison.OrdinalIgnoreCase));
            var isSuccess = ObservedDrives.Contains(key);
            var source = RescannedDrives.Contains(key) ? DiscoverySource.Scan : unrescannedSource;
            var timestamp = isSuccess ? rootStatus?.IndexStatus?.Block?.ScanTimestamp : null;
            var error = isSuccess ? null : Errors.GetValueOrDefault(key);

            if (rootStatus?.IndexStatus is { } observed)
            {
                snapshotUpdates?.SendUpdateMessage($"Index {root}: producer: {observed.Producer?.ToString() ?? "unavailable"}; " +
                    $"MFT unavailable: {observed.MftUnavailable?.ToString() ?? "none"}");
            }

            if (isSuccess && source == DiscoverySource.Snapshot && timestamp.HasValue)
            {
                snapshotUpdates?.SendUpdateMessage($"Using index snapshot for {root}: (scan timestamp {timestamp.Value:O}; may be stale)");
            }

            Observations.Add(new RootObservation(root, exactRoot, isSuccess, source, timestamp, error));
        }
    }

    /// <summary>
    /// Statuses, observed drives, rescans and errors are keyed by the index root key text (a volume letter or a normalized
    /// directory path), while scope roots are path-shaped, so each root is converted before it is looked up.
    /// </summary>
    static string ToStatusKey(string root) =>
        RootKeyProjection.ParseRootKeys([root]) is [var parsed] ? parsed.ToString() : root;

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

    public void AddExpansionErrorObservations()
    {
        foreach (var root in Scope.SearchRoots.Concat(Scope.RetainedRoots))
        {
            if (!Scope.ExpansionErrors.TryGetValue(root, out var expansionError))
            {
                continue;
            }

            if (Observations.Any(item => string.Equals(item.Root, root, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var isExact = Scope.RetainedRoots.Contains(root);
            Observations.Add(new RootObservation(root, isExact, false, DiscoverySource.Recursive, null, expansionError));
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
            RootStatuses = FinalStatuses,
            Errors = Errors,
            WatchExcludedRoots = Array.AsReadOnly<string>([.. FinalStatuses
                .Where(item => item.IndexStatus?.Producer != ProducerKind.Mft || Errors.ContainsKey(item.Drive))
                .Select(item => item.Drive)])
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

    static bool IsFailed(RootStatus indexStatus) =>
        indexStatus.State is RootState.Failed or RootState.Offline ||
        indexStatus.Failure is not null ||
        indexStatus.Block is { IsCapacityExhausted: true } or { AccessDeniedSubtreeCount: > 0 };

    static string GetIndexStatusFailureMessage(RootStatus indexStatus)
    {
        if (indexStatus.Block is { IsCapacityExhausted: true })
        {
            return $"The index block for {indexStatus.Root} ran out of capacity before the scan finished.";
        }
        if (indexStatus.Block is { AccessDeniedSubtreeCount: > 0 } block)
        {
            return $"{block.AccessDeniedSubtreeCount} subtree(s) under {indexStatus.Root} could not be entered.";
        }
        return indexStatus.Failure?.Message ?? $"Drive is {indexStatus.State}.";
    }
}
