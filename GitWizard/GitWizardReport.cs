using System.Text.Json;
using System.Text.Json.Serialization;
using LibGit2Sharp;

namespace GitWizard
{
    [Serializable]
    public class GitWizardReport
    {
        [Serializable]
        public class Repository
        {
            public readonly string WorkingDirectory;
            public string? CurrentBranch { get; private set; }
            public bool IsDetachedHead { get; private set; }
            public SortedDictionary<string, Repository?>? Submodules { get; private set; }

            public Repository(string workingDirectory)
            {
                WorkingDirectory = workingDirectory;
            }

            public async Task Refresh()
            {
                var repository = new LibGit2Sharp.Repository(WorkingDirectory);
                CurrentBranch = repository.Head.FriendlyName;
                IsDetachedHead = !(repository.Head.Reference is SymbolicReference);

                foreach (var submodule in repository.Submodules)
                {
                    Submodules ??= new SortedDictionary<string, Repository?>();

                    var path = Path.Combine(WorkingDirectory, submodule.Path);
                    if (!Submodules.TryGetValue(path, out var submoduleRepository))
                    {
                        submoduleRepository = LibGit2Sharp.Repository.IsValid(path) ? new Repository(path) : null;
                        Submodules[path] = submoduleRepository;
                    }

                    if (submoduleRepository == null)
                        continue;

                    //TODO: Should be using AwaitAll?
                    await Task.Run(submoduleRepository.Refresh);
                }
            }
        }

        static GitWizardReport? _cachedReport;

        public SortedSet<string> SearchPaths { get; set; } = new();
        public SortedSet<string> IgnoredPaths { get; set; } = new();
        public SortedDictionary<string, Repository> Repositories { get; set; } = new();

        public static string GetCachedReportPath()
        {
            return Path.Combine(GitWizardAPI.GetCachePath(), "report.json");
        }

        public static GitWizardReport? GetCachedReport()
        {
            if (_cachedReport != null)
                return _cachedReport;

            var globalConfigurationPath = GetCachedReportPath();
            if (!File.Exists(globalConfigurationPath))
                return _cachedReport;

            // TODO: Async config load (file read)
            var jsonText = File.ReadAllText(globalConfigurationPath);
            _cachedReport = JsonSerializer.Deserialize<GitWizardReport>(jsonText);
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
        public static async Task<GitWizardReport> GenerateReport(GitWizardConfiguration configuration,
            ICollection<string>? repositoryPaths = null, Action<string>? onUpdate = null)
        {
            var report = new GitWizardReport(configuration);
            if (repositoryPaths == null)
            {
                repositoryPaths = new SortedSet<string>();
                await report.GetRepositoryPaths(repositoryPaths, onUpdate);
            }

            await report.Refresh(repositoryPaths);

            return report;
        }

        async Task GetRepositoryPaths(ICollection<string> repositoryPaths, Action<string>? onUpdate = null)
        {
            foreach (var path in SearchPaths)
            {
                await GitWizardAPI.GetRepositoryPaths(path, repositoryPaths, IgnoredPaths, onUpdate);
            }
        }

        public async Task Refresh(ICollection<string> repositoryPaths)
        {
            foreach (var path in repositoryPaths)
            {
                if (!Repositories.TryGetValue(path, out var repository))
                {
                    repository = new Repository(path);
                    Repositories[path] = repository;
                }

                await Task.Run(repository.Refresh);
            }
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
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
                }));
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: Failed to save report to path: {path}. Exception details to follow.");
                Console.WriteLine(e.Message);
                Console.WriteLine(e.StackTrace);
            }
        }
    }
}
