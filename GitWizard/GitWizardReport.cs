using System.Text.Json;

namespace GitWizard
{
    [Serializable]
    public class GitWizardReport
    {
        [Serializable]
        public class Repository
        {
            public string Path = string.Empty;
            public string CurrentBranch = string.Empty;
        }

        static GitWizardReport? _cachedReport;

        public SortedSet<string> SearchPaths { get; set; } = new();
        public SortedSet<string> IgnoredPaths { get; set; } = new();

        public static string GetCachedReportPath()
        {
            return Path.Combine(GitWizardAPI.GetCachePath(), "report.json");
        }

        public static GitWizardReport? GetCachedReport()
        {
            if (_cachedReport == null)
            {
                var globalConfigPath = GetCachedReportPath();
                if (File.Exists(globalConfigPath))
                {
                    // TODO: Async config load (file read)
                    var jsonText = File.ReadAllText(globalConfigPath);
                    _cachedReport = JsonSerializer.Deserialize<GitWizardReport>(jsonText);
                }
            }

            return _cachedReport;
        }

        public GitWizardReport() { }

        public GitWizardReport(GitWizardConfig config)
        {
            SearchPaths = config.SearchPaths;
            IgnoredPaths = config.IgnoredPaths;
        }

        public static async Task<GitWizardReport> GenerateReport(GitWizardConfig config, Action<string>? onUpdate = null)
        {
            var report = new GitWizardReport(config);
            await report.Refresh(onUpdate);
            return report;
        }

        public async Task Refresh(Action<string>? onUpdate = null)
        {
            var repositoryPaths = new SortedSet<string>();
            foreach (var path in SearchPaths)
            {
                await GitWizardAPI.GetRepositoryPaths(path, repositoryPaths, IgnoredPaths, onUpdate);
            }
        }
    }
}
