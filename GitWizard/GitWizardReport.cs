using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitWizard.Discovery;

namespace GitWizard;

[Serializable]
public partial class GitWizardReport : IAsyncDisposable
{
    static GitWizardReport? _cachedReport;
    static readonly Lock ReportLock = new();

    internal static void ResetCachedReport()
    {
        lock (ReportLock)
        {
            _cachedReport = null;
        }
    }

    /// <summary>Shared serializer options for report (de)serialization and the
    /// DOM-level merge. Matches the inline options used by <see cref="Save"/>.</summary>
    static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = true,
    };

    /// <summary>
    /// The current schema version emitted by this build. Stamped onto every
    /// report at save time so cached reports from older builds don't
    /// propagate stale version strings forward.
    /// </summary>
    public const string CurrentSchemaVersion = "2.1";

    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Documents what the per-repo <c>Branches</c> lists contain: "actionable"
    /// (the default - merged-but-behind + unmerged-ahead branches only) or
    /// "all" (full inventory, via --all-branches). Consumers must not assume
    /// Branches is exhaustive unless this is "all".
    /// </summary>
    public string? BranchScope { get; set; }

    public SortedSet<string> SearchPaths { get; set; } = new();
    public SortedSet<string> IgnoredPaths { get; set; } = new();

    /// <summary>
    /// Whether this report's scan should skip directories whose last path segment
    /// starts with '.'. Mirrors <see cref="GitWizardConfiguration.SkipHiddenDirectories"/>.
    /// <c>null</c> (the default) includes hidden directories.
    /// </summary>
    public bool? SkipHiddenDirectories { get; set; }

    /// <summary>
    /// Whether this report's scan includes temp directories. Mirrors
    /// <see cref="GitWizardConfiguration.IncludeTempDirectories"/>. <c>null</c> (the default)
    /// excludes them.
    /// </summary>
    public bool? IncludeTempDirectories { get; set; }

    public SortedDictionary<string, GitWizardRepository> Repositories { get; set; } = new();
    public HashSet<string> DeletedPaths { get; private set; } = new();

    /// <summary>
    /// Cached paths whose directory exists but no longer resolves to a valid git repository
    /// (e.g. the <c>.git</c> folder was removed). Populated by <see cref="Refresh"/> and pruned
    /// from <see cref="Repositories"/>, distinct from <see cref="DeletedPaths"/> (missing
    /// directory, which may just be a transiently-unmounted drive).
    /// </summary>
    public HashSet<string> NonRepositoryPaths { get; private set; } = new();

    /// <summary>Immutable inventory from the discovery pass, or null if explicit paths were used or report was deserialized.</summary>
    [JsonIgnore]
    public DiscoveryInventory? DiscoveryInventory { get; internal set; }

    [JsonPropertyName("run")]
    public ReportRunSummary? Run { get; set; }

    public static string GetCachedReportPath()
    {
        return Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json");
    }

    public static GitWizardReport? GetCachedReport()
    {
        lock (ReportLock)
        {
            if (_cachedReport != null)
            {
                return _cachedReport;
            }
        }

        var reportPath = GetCachedReportPath();
        if (!File.Exists(reportPath))
        {
            return null;
        }

        string jsonText;
        try
        {
            jsonText = File.ReadAllText(reportPath);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Failed to get cached report.");
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<GitWizardReport>(jsonText);
            lock (ReportLock)
            {
                _cachedReport = report;
            }
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception,
                $"Failed to deserialize cached report.\nYou may need to modify or delete the file at {reportPath}.\n");
        }

        lock (ReportLock)
        {
            return _cachedReport;
        }
    }

    public static async Task<GitWizardReport?> GetCachedReportAsync(CancellationToken cancellationToken = default)
    {
        lock (ReportLock)
        {
            if (_cachedReport != null)
            {
                return _cachedReport;
            }
        }

        var reportPath = GetCachedReportPath();
        if (!File.Exists(reportPath))
        {
            return null;
        }

        string jsonText;
        try
        {
            jsonText = await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Failed to get cached report.");
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<GitWizardReport>(jsonText);
            lock (ReportLock)
            {
                _cachedReport = report;
            }
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception,
                $"Failed to deserialize cached report.\nYou may need to modify or delete the file at {reportPath}.\n");
        }

        lock (ReportLock)
        {
            return _cachedReport;
        }
    }

    public GitWizardReport()
    {
    }

    public GitWizardReport(GitWizardConfiguration configuration)
    {
        SearchPaths = configuration.SearchPaths;
        IgnoredPaths = configuration.IgnoredPaths;
        SkipHiddenDirectories = configuration.SkipHiddenDirectories;
        IncludeTempDirectories = configuration.IncludeTempDirectories;
    }

    /// <summary>
    /// Generate a report with the given configuration
    /// </summary>
    /// <param name="configuration">The configuration to use for this report.</param>
    /// <param name="repositoryPaths">The repository paths to include in the report.</param>
    /// <param name="updateHandler">Optional handler for UI updates.</param>
    /// <param name="options">Optional refresh flags; defaults to all-off when omitted.</param>
    /// <returns>The generated report.</returns>
    public static async Task<GitWizardReport> GenerateReportAsync(GitWizardConfiguration configuration,
        ICollection<string>? repositoryPaths = null, IUpdateHandler? updateHandler = null,
        GitWizardReportOptions? options = null)
    {
        options ??= new GitWizardReportOptions();

        var report = new GitWizardReport(configuration);
        try
        {
            if (repositoryPaths == null)
            {
                var discovered = new SortedSet<string>();
                await report.GetRepositoryPathsAsync(discovered, updateHandler, options.NoMft, options.RetainDiscoveryIndex, options).ConfigureAwait(false);
                repositoryPaths = discovered;
            }

            var pathsToRefresh = report.DiscoveryInventory is { CanPublishFullInventory: false } ? [.. report.DiscoveryInventory.Paths] : repositoryPaths;
            report.Refresh(pathsToRefresh, updateHandler, options.FetchRemotes, options.DeepRefresh, options.AllBranches, options.ComputeLocalCommitCount);
            report.BranchScope = options.AllBranches ? "all" : "actionable";

            return report;
        }
        catch
        {
            await report.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Refresh(ICollection<string> repositoryPaths, IUpdateHandler? updateHandler = null,
        bool fetchRemotes = false, bool deepRefresh = false, bool allBranches = false,
        bool computeLocalCommitCount = true)
    {
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
        var validPaths = PruneDeletedRepositories(repositoryPaths);

        var count = 0;

        try
        {
            updateHandler?.StartProgress("Scanning repositories", validPaths.Count);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception thrown by Refresh StartProgress callback.");
        }

        using (GitWizardRepository.UnresolvedUpstreamDiagnosticScope.Open())
        {
            Parallel.ForEach(validPaths, parallelOptions, path =>
            {
                RefreshSingleRepository(path, updateHandler, fetchRemotes, deepRefresh, allBranches, computeLocalCommitCount);

                try
                {
                    updateHandler?.UpdateProgress(Interlocked.Increment(ref count));
                }
                catch (Exception exception)
                {
                    GitWizardLog.LogException(exception, "Exception thrown by Refresh UpdateProgress callback.");
                }
            });
        }

        PruneNonRepositoryPaths(validPaths);
    }

    // Identifies paths that existed but whose Refresh determined they are no longer a valid git
    // repository (NotAGitRepositoryError), records them on NonRepositoryPaths, and prunes them
    // from Repositories so they stop being re-probed as stale cache entries every session. This
    // is deliberately separate from PruneDeletedRepositories: a missing directory can be a
    // transiently-unmounted drive and must NOT be pruned, whereas a directory that exists but
    // isn't a repo is unambiguously stale.
    void PruneNonRepositoryPaths(IEnumerable<string> refreshedPaths)
    {
        var stalePaths = new List<string>();

        // Locked for the whole read-then-write pass: an in-flight orphan refresh from a
        // concurrent Refresh call (see RefreshSingleRepository) mutates the same dictionary
        // with its own lock (Repositories), so TryGetValue and the later Remove must be
        // serialized against it to avoid observing or leaving Repositories in a torn state.
        lock (Repositories)
        {
            foreach (var path in refreshedPaths)
            {
                if (Repositories.TryGetValue(path, out var repository) &&
                    repository.RefreshError == GitWizardRepository.NotAGitRepositoryError)
                {
                    stalePaths.Add(path);
                }
            }

            NonRepositoryPaths = new HashSet<string>(stalePaths);

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

    // Identifies repos whose directory no longer exists, records them on DeletedPaths, prunes
    // them from Repositories, and returns the remaining still-present paths to refresh.
    ConcurrentBag<string> PruneDeletedRepositories(ICollection<string> repositoryPaths)
    {
        var validPaths = new ConcurrentBag<string>();
        var deletedPaths = new ConcurrentBag<string>();

        foreach (var path in repositoryPaths)
        {
            if (Directory.Exists(path))
            {
                validPaths.Add(path);
            }
            else
            {
                deletedPaths.Add(path);
            }
        }

        DeletedPaths = new HashSet<string>(deletedPaths);

        // Locked for the same reason as PruneNonRepositoryPaths: this Remove races the
        // dictionary against RefreshSingleRepository's lock (Repositories) add-if-missing
        // from a concurrent Refresh call on the same report.
        lock (Repositories)
        {
            foreach (var deleted in DeletedPaths)
            {
                Repositories.Remove(deleted);
            }
        }

        if (DeletedPaths.Count > 0)
        {
            GitWizardLog.Log($"Cleaned {DeletedPaths.Count} deleted repository(s) from cache");
        }

        return validPaths;
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
    void RefreshSingleRepository(string path, IUpdateHandler? updateHandler, bool fetchRemotes,
        bool deepRefresh, bool allBranches, bool computeLocalCommitCount)
    {
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
            updateHandler?.OnRepositoryCreated(repository);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception thrown by Refresh OnRepositoryCreated callback.");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var cancellationTokenSource = new CancellationTokenSource(RefreshTimeout);
        repository.Refresh(updateHandler, fetchRemotes, deepRefresh, allBranches, computeLocalCommitCount, cancellationTokenSource.Token);

        stopwatch.Stop();
        repository.RefreshTimeSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
        if (stopwatch.Elapsed.TotalSeconds >= 10)
        {
            GitWizardLog.Log($"Refresh took {repository.RefreshTimeSeconds}s for {path}", GitWizardLog.LogType.Warning);
        }
    }

    public IEnumerable<string> GetRepositoryPaths()
    {
        // Locked and materialized into a list up front rather than yielded lazily: an
        // iterator's lock would otherwise stay held across the yield return suspension
        // (held for as long as the caller takes to consume the enumerable), which could
        // block a concurrent Refresh call indefinitely instead of just for the snapshot copy.
        lock (Repositories)
        {
            return new List<string>(Repositories.Keys);
        }
    }
}
