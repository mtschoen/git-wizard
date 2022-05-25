using LibGit2Sharp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace GitWizard;

[Serializable]
public class GitWizardReport
{
    [Serializable]
    public class Repository
    {
        public string? WorkingDirectory { get; private set; }
        public string? CurrentBranch { get; private set; }
        public bool IsDetachedHead { get; private set; }
        public bool HasPendingChanges { get; private set; }
        public SortedDictionary<string, Repository?>? Submodules { get; private set; }

        Repository()
        {
        }

        public Repository(string workingDirectory)
        {
            WorkingDirectory = workingDirectory;
        }

        public void Refresh()
        {
            if (string.IsNullOrEmpty(WorkingDirectory) || !Directory.Exists(WorkingDirectory))
            {
                GitWizardLog.Log($"Working directory {WorkingDirectory} is invalid.", GitWizardLog.LogType.Error);
                return;
            }

            Console.WriteLine($"Refreshing {WorkingDirectory}");

            try
            {
                var repository = new LibGit2Sharp.Repository(WorkingDirectory);
                CurrentBranch = repository.Head.FriendlyName;
                IsDetachedHead = repository.Head.Reference is not SymbolicReference;
                var status = repository.RetrieveStatus();
                HasPendingChanges = status.IsDirty;

                Parallel.ForEach(repository.Submodules, submodule =>
                {
                    Submodules ??= new SortedDictionary<string, Repository?>();

                    var path = Path.Combine(WorkingDirectory, submodule.Path);
                    if (!Submodules.TryGetValue(path, out var submoduleRepository))
                    {
                        submoduleRepository = LibGit2Sharp.Repository.IsValid(path) ? new Repository(path) : null;
                        Submodules[path] = submoduleRepository;
                    }

                    if (submoduleRepository == null)
                        return;

                    submoduleRepository.Refresh();
                });
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, $"Exception thrown trying to refresh {WorkingDirectory}");
            }
        }
    }

    static GitWizardReport? _cachedReport;

    public SortedSet<string> SearchPaths { get; set; } = new();
    public SortedSet<string> IgnoredPaths { get; set; } = new();
    public SortedDictionary<string, Repository> Repositories { get; set; } = new();

    public static string GetCachedReportPath()
    {
        return Path.Combine(GitWizardApi.GetCachePath(), "report.json");
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
    /// <param name="onUpdate">Optional callback for reporting progress</param>
    /// <returns>Task containing the report</returns>
    public static GitWizardReport GenerateReport(GitWizardConfiguration configuration,
        ICollection<string>? repositoryPaths = null, Action<string>? onUpdate = null)
    {
        var report = new GitWizardReport(configuration);
        if (repositoryPaths == null)
        {
            repositoryPaths = new SortedSet<string>();
            report.GetRepositoryPaths(repositoryPaths, onUpdate);
        }

        report.Refresh(repositoryPaths, onUpdate);

        return report;
    }

    void GetRepositoryPaths(ICollection<string> repositoryPaths, Action<string>? onUpdate = null)
    {
        Parallel.ForEach(SearchPaths, path =>
        {
            GitWizardApi.GetRepositoryPaths(path, repositoryPaths, IgnoredPaths, onUpdate);
        });
    }

    public void Refresh(ICollection<string> repositoryPaths, Action<string>? onUpdate = null)
    {
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
                onUpdate?.Invoke($"Refreshing {path}");
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, "Exception thrown by Refresh onUpdate callback.");
            }

            repository.Refresh();
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
