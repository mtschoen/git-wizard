namespace GitWizard;

public partial class GitWizardReport
{
    internal sealed record RepositoryRefreshContext(
        IUpdateHandler? UpdateHandler,
        bool FetchRemotes,
        bool DeepRefresh,
        bool AllBranches,
        bool ComputeLocalCommitCount,
        NonRepositoryProbeCache ProbeCache);

    // Missing directories can be transiently unmounted and remain separate in DeletedPaths.
    // Existing paths with a remembered failed probe are removed from the repository inventory.
    void PruneNonRepositoryPaths(IEnumerable<string> refreshedPaths, NonRepositoryProbeCache probeCache)
    {
        var stalePaths = new List<string>();

        // An in-flight orphan refresh from a concurrent Refresh call can mutate Repositories
        // with its own lock, so removals must use that same lock.
        lock (Repositories)
        {
            foreach (var path in refreshedPaths)
            {
                if (probeCache.IsNonRepositoryThisRefresh(path))
                {
                    stalePaths.Add(path);
                }
            }

            NonRepositoryPaths = [.. stalePaths];

            foreach (var stale in NonRepositoryPaths)
            {
                Repositories.Remove(stale);
            }
        }

        if (NonRepositoryPaths.Count > 0)
        {
            GitWizardLog.Log($"Pruned {NonRepositoryPaths.Count} stale non-repo path(s) from cache");
        }
    }

    /// <summary>
    /// Timeout for refreshing a single repository. Defaults to 5 minutes.
    /// Internal so tests can shorten it without sleeping. Assumes tests run sequentially
    /// (NUnit test fixtures in this solution do not enable LevelOfParallelism).
    /// </summary>
    internal static TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromMinutes(5);

    // Gets or creates the GitWizardRepository for path, runs its Refresh inline under a
    // CancellationTokenSource with RefreshTimeout, and records the elapsed time. One iteration
    // of the Refresh Parallel.ForEach body. Runs inline without Task.Run to prevent thread-pool
    // starvation under large reports (docs/refresh-concurrency-review.md hazard 7).
    void RefreshSingleRepository(string path, RepositoryRefreshContext context)
    {
        if (context.ProbeCache.ShouldSkip(path, out var lastWriteTimeUtc))
        {
            return;
        }

        GitWizardRepository? repository;
        lock (Repositories)
        {
            if (!Repositories.TryGetValue(path, out repository))
            {
                repository = new GitWizardRepository(path);
                Repositories[path] = repository;
            }
        }

        try
        {
            context.UpdateHandler?.OnRepositoryCreated(repository);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception thrown by Refresh OnRepositoryCreated callback.");
        }

        // A fetch rewrites .git after the index was read, so only a refresh that leaves the files alone may use it.
        if (!context.FetchRemotes && _indexedSizes is { } indexedSizes)
        {
            repository.OfferIndexedSizes(indexedSizes, path);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var cancellationTokenSource = new CancellationTokenSource(RefreshTimeout);
        var refreshOutcome = repository.RefreshWithOutcome(context.UpdateHandler, context.FetchRemotes,
            context.DeepRefresh, context.AllBranches, context.ComputeLocalCommitCount, cancellationTokenSource.Token);
        repository.WithdrawIndexedSize();
        if (refreshOutcome == RepositoryRefreshOutcome.NonRepository)
        {
            context.ProbeCache.Record(path, lastWriteTimeUtc);
        }
        else if (refreshOutcome is RepositoryRefreshOutcome.Success or RepositoryRefreshOutcome.OwnershipBlocked)
        {
            context.ProbeCache.Remove(path);
        }

        stopwatch.Stop();
        repository.RefreshTimeSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
        if (stopwatch.Elapsed.TotalSeconds >= 10)
        {
            GitWizardLog.Log($"Refresh took {repository.RefreshTimeSeconds}s for {path}", GitWizardLog.LogType.Warning);
        }
    }
}
