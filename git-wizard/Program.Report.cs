using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitWizard.CLI;

public static partial class Program
{
    static readonly JsonSerializerOptions IndentedSerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    static readonly JsonSerializerOptions CompactSerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    static async Task RunDefaultReportModeAsync(RunConfiguration runConfiguration, GitWizardConfiguration configuration)
    {
        var repositoryPaths = ParseExplicitPaths(runConfiguration) ?? await GetRepositoryPathsAsync(runConfiguration);
        var updateHandler = new UpdateHandler();
        var report = await GetReportAsync(runConfiguration, configuration, repositoryPaths, updateHandler);
        if (report == null)
        {
            // If the user requested to not generate a new report, and no cached report exists, early out
            GitWizardLog.Log("Could not retrieve cached report", GitWizardLog.LogType.Error);
            Environment.Exit(0);
            return;
        }

        GitWizardLog.Log("Processing queued commands...");
        updateHandler.ProcessCommands();
        updateHandler.PrintSummary();

        if (repositoryPaths == null)
            await GitWizardApi.SaveCachedRepositoryPathsAsync(report.GetRepositoryPaths());

        await SaveReportAsync(runConfiguration, report).ConfigureAwait(false);

        var filteredReport = ApplyFilter(runConfiguration, report);
        var needsAttention = ReportNeedsAttention(filteredReport);

        string jsonString;
        if (runConfiguration.Summary)
        {
            var summary = GitWizardSummary.FromReport(filteredReport);
            var options = runConfiguration.Minified ? CompactSerializerOptions : IndentedSerializerOptions;
            jsonString = JsonSerializer.Serialize(summary, options);
        }
        else
        {
            jsonString = SerializeReport(runConfiguration, filteredReport);
        }

        if (!GitWizardLog.SilentMode)
            Console.WriteLine(jsonString);

        if (needsAttention)
            Environment.Exit(1);
    }

    static async Task<GitWizardReport?> GetReportAsync(RunConfiguration runConfiguration, GitWizardConfiguration configuration,
        ICollection<string>? repositoryPaths, UpdateHandler updateHandler)
    {
        if (!runConfiguration.RebuildReport)
        {
            var cachedReport = await GitWizardReport.GetCachedReportAsync().ConfigureAwait(false);
            if (!runConfiguration.RefreshReport)
                return cachedReport;

            if (cachedReport != null && repositoryPaths != null)
            {
                cachedReport.Refresh(repositoryPaths, updateHandler, allBranches: runConfiguration.AllBranches, computeLocalCommitCount: !runConfiguration.NoLocalCommitCount);
                cachedReport.BranchScope = runConfiguration.AllBranches ? "all" : "actionable";
                return cachedReport;
            }
        }

        var report = await GitWizardReport.GenerateReportAsync(configuration, repositoryPaths, updateHandler,
            new GitWizardReportOptions { NoMft = runConfiguration.NoMft, AllBranches = runConfiguration.AllBranches, ComputeLocalCommitCount = !runConfiguration.NoLocalCommitCount });

        if (repositoryPaths == null)
            EnsureUsableSearchPaths(configuration);

        return report;
    }

    /// <summary>
    /// Handle the --merge flag: validate required args, refresh the supplied repos, and merge
    /// them into the existing report at --save-path (atomic write, other entries preserved).
    /// </summary>
    static void RunMerge(RunConfiguration runConfiguration, GitWizardConfiguration configuration)
    {
        var savePath = runConfiguration.SavePath;
        var explicitPaths = ParseExplicitPaths(runConfiguration);

        if (explicitPaths == null || explicitPaths.Length == 0 || string.IsNullOrEmpty(savePath))
        {
            GitWizardLog.Log(
                "--merge requires both --paths (repos to refresh) and --save-path (report to merge into).",
                GitWizardLog.LogType.Error);
            Environment.Exit(2);
            return;
        }

        GitWizardLog.Log($"Merging {explicitPaths.Length} repo(s) into {savePath}");
        var updateHandler = new UpdateHandler();
        GitWizardReport.MergeIntoFile(savePath, configuration, explicitPaths, updateHandler,
            allBranches: runConfiguration.AllBranches, computeLocalCommitCount: !runConfiguration.NoLocalCommitCount);
        updateHandler.ProcessCommands();
        updateHandler.PrintSummary();
    }

    static async Task SaveReportAsync(RunConfiguration runConfiguration, GitWizardReport report)
    {
        // Always save a cache
        await report.SaveAsync(GitWizardReport.GetCachedReportPath()).ConfigureAwait(false);

        var savePath = runConfiguration.SavePath;
        if (savePath == null)
            return;

        GitWizardLog.Log($"Saving report to {savePath}");

        await report.SaveAsync(savePath).ConfigureAwait(false);
    }

    static string SerializeReport(RunConfiguration runConfiguration, GitWizardReport report)
    {
        // Stamp the current schema version so console output never carries
        // a stale version loaded from an older cache.
        report.SchemaVersion = GitWizardReport.CurrentSchemaVersion;
        var options = runConfiguration.Minified ? CompactSerializerOptions : IndentedSerializerOptions;

        var jsonString = JsonSerializer.Serialize(report, options);
        return jsonString;
    }

    static GitWizardReport ApplyFilter(RunConfiguration runConfiguration, GitWizardReport report)
    {
        if (string.IsNullOrEmpty(runConfiguration.FilterPattern))
            return report;

        var filtered = new GitWizardReport
        {
            SchemaVersion = report.SchemaVersion,
            SearchPaths = report.SearchPaths,
            IgnoredPaths = report.IgnoredPaths,
            IncludeTempDirectories = report.IncludeTempDirectories
        };

        foreach (var kvp in report.Repositories)
        {
            if (kvp.Key.Contains(runConfiguration.FilterPattern, StringComparison.OrdinalIgnoreCase))
                filtered.Repositories[kvp.Key] = kvp.Value;
        }

        return filtered;
    }

    static bool ReportNeedsAttention(GitWizardReport report)
    {
        foreach (var kvp in report.Repositories)
        {
            if (kvp.Value.HasPendingChanges || kvp.Value.LocalOnlyCommits || kvp.Value.BehindRemoteCount > 0)
                return true;
        }

        return false;
    }

    static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < units.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return $"{size:0.##} {units[i]}";
    }
}
