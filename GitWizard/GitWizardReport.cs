using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitWizard;

[Serializable]
public class GitWizardReport
{
    static GitWizardReport? _cachedReport;

    public SortedSet<string> SearchPaths { get; set; } = new();
    public SortedSet<string> IgnoredPaths { get; set; } = new();
    public SortedDictionary<string, GitWizardRepository> Repositories { get; set; } = new();

    public static string GetCachedReportPath()
    {
        return Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json");
    }

    public static GitWizardReport? GetCachedReport()
    {
        if (_cachedReport != null)
            return _cachedReport;

        var globalConfigurationPath = GetCachedReportPath();
        if (!File.Exists(globalConfigurationPath))
            return _cachedReport;

        // TODO: Async config load (file read)
        string jsonText;
        try
        {
            jsonText = File.ReadAllText(globalConfigurationPath);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Failed to get cached report.");
            return null;
        }

        try
        {
            _cachedReport = JsonSerializer.Deserialize<GitWizardReport>(jsonText);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception,
                $"Failed to deserialize cached report.\nYou may need to modify or delete the file at {globalConfigurationPath}.\n");
        }

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
    /// <returns>Task containing the report</returns>
    public static GitWizardReport GenerateReport(GitWizardConfiguration configuration,
        ICollection<string>? repositoryPaths = null, IUpdateHandler? updateHandler = null)
    {
        var report = new GitWizardReport(configuration);
        if (repositoryPaths == null)
        {
            repositoryPaths = new SortedSet<string>();
            report.GetRepositoryPaths(repositoryPaths, updateHandler);
        }

        report.Refresh(repositoryPaths, updateHandler);

        return report;
    }

    public void GetRepositoryPaths(ICollection<string> repositoryPaths, IUpdateHandler? updateHandler = null)
    {
        // Try MFT scan first (Windows only) — handles elevation automatically
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = SearchPaths,
            IgnoredPaths = IgnoredPaths
        };

        if (GitWizardApi.TryFindAllRepositoriesUsingMft(configuration, repositoryPaths, updateHandler))
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

    public void Refresh(ICollection<string> repositoryPaths, IUpdateHandler? updateHandler = null)
    {
        var count = 0;

        try
        {
            updateHandler?.StartProgress("Scanning repositories", repositoryPaths.Count);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception thrown by Refresh StartProgress callback.");
        }

        Parallel.ForEach(repositoryPaths, path =>
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
            var refreshTask = Task.Run(() => repository.Refresh(updateHandler));
            if (!refreshTask.Wait(TimeSpan.FromMinutes(5)))
            {
                repository.RefreshError = "Timed out after 5 minutes";
                GitWizardLog.Log($"Refresh timed out after 5 minutes for {path}", GitWizardLog.LogType.Warning);
            }
            else if (refreshTask.IsFaulted)
            {
                repository.RefreshError = refreshTask.Exception?.InnerException?.Message;
                GitWizardLog.Log($"Refresh failed for {path}: {repository.RefreshError}",
                    GitWizardLog.LogType.Error);
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
            // TODO: Async config save
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
                WriteIndented = true
            }));
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
