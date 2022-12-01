using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace GitWizard;

[Serializable]
public class GitWizardReport
{
    static GitWizardReport? _cachedReport;

    public SortedSet<string> SearchPaths { get; set; } = new();
    public SortedSet<string> IgnoredPaths { get; set; } = new();
    public SortedDictionary<string, Repository> Repositories { get; set; } = new();

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
            _cachedReport = JsonConvert.DeserializeObject<GitWizardReport>(jsonText);
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
        var count = 0;
        updateHandler?.StartProgress("Getting Repository Paths", SearchPaths.Count);
        Parallel.ForEach(SearchPaths, path =>
        {
            GitWizardApi.GetRepositoryPaths(path, repositoryPaths, IgnoredPaths, updateHandler);
            updateHandler?.UpdateProgress(++count);
        });
    }

    public void Refresh(ICollection<string> repositoryPaths, IUpdateHandler? updateHandler = null)
    {
        var count = 0;
        updateHandler?.StartProgress("Scanning repositories", repositoryPaths.Count);
        Parallel.ForEach(repositoryPaths, path =>
        {
            Repository? repository;
            lock (Repositories)
            {
                if (!Repositories.TryGetValue(path, out repository))
                {
                    repository = new Repository(path);
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

            updateHandler?.UpdateProgress(++count);

            repository.Refresh(updateHandler);
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
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore
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
