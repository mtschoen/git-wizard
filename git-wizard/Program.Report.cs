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

    internal static async Task<int> RunDefaultReportCoreAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator? coordinator = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        var explicitPaths = ParseExplicitPaths(runConfiguration);
        var isFullInventory = explicitPaths == null;

        var updateHandler = new UpdateHandler();
        GitWizardReport report;

        if (isFullInventory)
        {
            if (!runConfiguration.RebuildReport)
            {
                var cachedReport = await GitWizardReport.GetCachedReportAsync(cancellationToken).ConfigureAwait(false);
                if (!runConfiguration.RefreshReport && cachedReport != null)
                {
                    await using (cachedReport)
                    {
                        return OutputReport(cachedReport, runConfiguration, stdout);
                    }
                }
            }

            if (!EnsureUsableSearchPaths(configuration, _ => { }))
                return 1;

            var reportOptions = new GitWizardReportOptions
            {
                NoMft = runConfiguration.NoMft,
                AllBranches = runConfiguration.AllBranches,
                ComputeLocalCommitCount = !runConfiguration.NoLocalCommitCount,
                DiscoveryCoordinator = coordinator,
                DiscoveryPurpose = runConfiguration.RebuildRepositoryList ? DiscoveryPurpose.Rebuild : DiscoveryPurpose.Ordinary,
                AllowRepositoryList = !runConfiguration.RebuildRepositoryList
            };

            report = await GitWizardReport.GenerateReportAsync(configuration, null, updateHandler, reportOptions).ConfigureAwait(false);
        }
        else
        {
            var reportOptions = new GitWizardReportOptions
            {
                NoMft = runConfiguration.NoMft,
                AllBranches = runConfiguration.AllBranches,
                ComputeLocalCommitCount = !runConfiguration.NoLocalCommitCount
            };
            report = await GitWizardReport.GenerateReportAsync(configuration, explicitPaths, updateHandler, reportOptions).ConfigureAwait(false);
        }

        await using (report)
        {
            var exitCode = GetDiscoveryExitCode(report.DiscoveryInventory, isFullInventory);
            if (exitCode is { } failureExitCode)
            {
                if (report.DiscoveryInventory is not null)
                    WriteDiscoveryDiagnostics(report.DiscoveryInventory, stderr);
                return failureExitCode;
            }

            updateHandler.ProcessCommands();
            updateHandler.PrintSummary();

            await SaveReportAsync(runConfiguration, report).ConfigureAwait(false);

            return OutputReport(report, runConfiguration, stdout);
        }
    }

    static int OutputReport(GitWizardReport report, RunConfiguration runConfiguration, TextWriter stdout)
    {
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

        if (!GitWizardLog.SilentMode && !runConfiguration.Silent)
            stdout.WriteLine(jsonString);

        return needsAttention ? 1 : 0;
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
        if (report.DiscoveryInventory is { CanPublishFullInventory: false })
            throw new InvalidOperationException("Incomplete discovery report cannot be saved.");

        // Always save a cache
        await report.SaveAsync(GitWizardReport.GetCachedReportPath()).ConfigureAwait(false);

        var savePath = runConfiguration.SavePath;
        if (savePath == null)
            return;

        GitWizardLog.Log($"Saving report to {savePath}");

        await report.SaveAsync(savePath).ConfigureAwait(false);
    }

    internal static string SerializeReport(RunConfiguration runConfiguration, GitWizardReport report)
    {
        // Stamp the current schema version so console output never carries
        // a stale version loaded from an older cache.
        report.SchemaVersion = GitWizardReport.CurrentSchemaVersion;
        var options = runConfiguration.Minified ? CompactSerializerOptions : IndentedSerializerOptions;

        var jsonString = JsonSerializer.Serialize(report, options);
        return jsonString;
    }

    internal static GitWizardReport ApplyFilter(RunConfiguration runConfiguration, GitWizardReport report)
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

    internal static bool ReportNeedsAttention(GitWizardReport report)
    {
        foreach (var kvp in report.Repositories)
        {
            if (kvp.Value.HasPendingChanges || kvp.Value.LocalOnlyCommits || kvp.Value.BehindRemoteCount > 0)
                return true;
        }

        return false;
    }
}
