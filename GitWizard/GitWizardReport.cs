using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GitWizard;

[Serializable]
public partial class GitWizardReport
{
    static GitWizardReport? _cachedReport;
    static readonly object ReportLock = new();

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
    public const string CurrentSchemaVersion = "2.0";

    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Documents what the per-repo <c>Branches</c> lists contain: "actionable"
    /// (the default — merged-but-behind + unmerged-ahead branches only) or
    /// "all" (full inventory, via --all-branches). Consumers must not assume
    /// Branches is exhaustive unless this is "all".
    /// </summary>
    public string? BranchScope { get; set; }

    public SortedSet<string> SearchPaths { get; set; } = new();
    public SortedSet<string> IgnoredPaths { get; set; } = new();
    public SortedDictionary<string, GitWizardRepository> Repositories { get; set; } = new();
    public HashSet<string> DeletedPaths { get; private set; } = new();

    public static string GetCachedReportPath()
    {
        return Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json");
    }

    public static GitWizardReport? GetCachedReport()
    {
        lock (ReportLock)
        {
            if (_cachedReport != null)
                return _cachedReport;
        }

        var reportPath = GetCachedReportPath();
        if (!File.Exists(reportPath))
            return null;

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
                _cachedReport = report;
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception,
                $"Failed to deserialize cached report.\nYou may need to modify or delete the file at {reportPath}.\n");
        }

        lock (ReportLock)
            return _cachedReport;
    }

    public static async Task<GitWizardReport?> GetCachedReportAsync(CancellationToken cancellationToken = default)
    {
        lock (ReportLock)
        {
            if (_cachedReport != null)
                return _cachedReport;
        }

        var reportPath = GetCachedReportPath();
        if (!File.Exists(reportPath))
            return null;

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
                _cachedReport = report;
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception,
                $"Failed to deserialize cached report.\nYou may need to modify or delete the file at {reportPath}.\n");
        }

        lock (ReportLock)
            return _cachedReport;
    }

    public GitWizardReport()
    {
    }

    public GitWizardReport(GitWizardConfiguration configuration)
    {
        SearchPaths = configuration.SearchPaths;
        IgnoredPaths = configuration.IgnoredPaths;
    }

    /// <summary>
    /// Generate a report with the given configuration
    /// </summary>
    /// <param name="configuration">The configuration to use for this report.</param>
    /// <param name="repositoryPaths">The repository paths to include in the report.</param>
    /// <param name="updateHandler">Optional handler for UI updates.</param>
    /// <param name="fetchRemotes">When true, fetch from each repository's remotes before computing ahead/behind state.</param>
    /// <param name="deepRefresh">When true, run the expensive <c>git update-index --refresh</c> on each repository.</param>
    /// <param name="noMft">When true, skip MFT-based discovery and walk the filesystem directly.</param>
    /// <param name="allBranches">When true, include the default branch and branches sitting at the default tip.</param>
    /// <returns>The generated report.</returns>
    public static GitWizardReport GenerateReport(GitWizardConfiguration configuration,
        ICollection<string>? repositoryPaths = null, IUpdateHandler? updateHandler = null,
        bool fetchRemotes = false, bool deepRefresh = false, bool noMft = false,
        bool allBranches = false)
    {
        var report = new GitWizardReport(configuration);
        if (repositoryPaths == null)
        {
            repositoryPaths = new SortedSet<string>();
            report.GetRepositoryPaths(repositoryPaths, updateHandler, noMft);
        }

        report.Refresh(repositoryPaths, updateHandler, fetchRemotes, deepRefresh, allBranches);
        report.BranchScope = allBranches ? "all" : "actionable";

        return report;
    }

    public void GetRepositoryPaths(ICollection<string> repositoryPaths, IUpdateHandler? updateHandler = null,
        bool noMft = false)
    {
        // Try MFT scan first (Windows only) — handles elevation automatically
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = SearchPaths,
            IgnoredPaths = IgnoredPaths
        };

        if (GitWizardApi.TryFindAllRepositoriesUsingMft(configuration, repositoryPaths, updateHandler, noMft))
            return;

        // Fall back to recursive directory scan
        var count = 0;

        try
        {
            updateHandler?.StartProgress("Getting GitWizardRepository Paths", SearchPaths.Count);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception thrown by GetRepositoryPaths StartProgress callback.");
        }

        Parallel.ForEach(SearchPaths, path =>
        {
            GitWizardApi.GetRepositoryPaths(path, repositoryPaths, IgnoredPaths, updateHandler);

            try
            {
                updateHandler?.UpdateProgress(++count);
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, "Exception thrown by GetRepositoryPaths UpdateProgress callback.");
            }
        });
    }

    public void Refresh(ICollection<string> repositoryPaths, IUpdateHandler? updateHandler = null,
        bool fetchRemotes = false, bool deepRefresh = false, bool allBranches = false)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
        var validPaths = new ConcurrentBag<string>();
        var deletedPaths = new ConcurrentBag<string>();

        // Pre-filter: identify deleted repos (directory no longer exists)
        foreach (var path in repositoryPaths)
        {
            if (Directory.Exists(path))
                validPaths.Add(path);
            else
                deletedPaths.Add(path);
        }

        DeletedPaths = new HashSet<string>(deletedPaths);

        // Clean up deleted repos from the report's Repositories dictionary
        foreach (var deleted in DeletedPaths)
        {
            Repositories.Remove(deleted);
        }

        if (DeletedPaths.Count > 0)
        {
            GitWizardLog.Log($"Cleaned {DeletedPaths.Count} deleted repository(s) from cache");
        }

        var count = 0;

        try
        {
            updateHandler?.StartProgress("Scanning repositories", validPaths.Count);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception thrown by Refresh StartProgress callback.");
        }

        Parallel.ForEach(validPaths, options, path =>
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
            var refreshTask = Task.Run(() => repository.Refresh(updateHandler, fetchRemotes, deepRefresh, allBranches));
            if (!refreshTask.Wait(TimeSpan.FromMinutes(5)))
            {
                GitWizardLog.Log($"Refresh timed out after 5 minutes for {path}", GitWizardLog.LogType.Warning);
                repository.MarkRefreshFailed("Timed out after 5 minutes", updateHandler);
            }
            else if (refreshTask.IsFaulted)
            {
                var errorMsg = refreshTask.Exception?.InnerException?.Message ?? "Unknown error";
                GitWizardLog.Log($"Refresh failed for {path}: {errorMsg}", GitWizardLog.LogType.Error);
                // Note: OnRepositoryRefreshCompleted is called from the catch block in Refresh()
            }

            stopwatch.Stop();
            repository.RefreshTimeSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
            if (stopwatch.Elapsed.TotalSeconds >= 10)
            {
                GitWizardLog.Log($"Refresh took {repository.RefreshTimeSeconds}s for {path}", GitWizardLog.LogType.Warning);
            }

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

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        try
        {
            SchemaVersion = CurrentSchemaVersion;
            File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Failed to save report to path: {path}.");
        }
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        try
        {
            SchemaVersion = CurrentSchemaVersion;
            var jsonText = JsonSerializer.Serialize(this, SerializerOptions);
            await File.WriteAllTextAsync(path, jsonText, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Failed to save report to path: {path}.");
        }
    }

    public IEnumerable<string> GetRepositoryPaths()
    {
        foreach (var kvp in Repositories)
        {
            yield return kvp.Key;
        }
    }
}
