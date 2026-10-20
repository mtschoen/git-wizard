using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitWizard.CLI;

public static partial class Program
{
    internal static async Task<int> RunDefaultReportCoreAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration? configuration = null,
        RepositoryDiscoveryCoordinator? coordinator = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;
        var updateHandler = new UpdateHandler(runConfiguration.Silent || GitWizardLog.SilentMode ? TextWriter.Null : stderr);
        using var logScope = new ReportLogScope(updateHandler, runConfiguration, stdout, stderr);
        var originalLogMethod = GitWizardLog.LogMethod;
        try
        {
            GitWizardLog.LogMethod = stderr.WriteLine;
            configuration = await PrepareInteractiveRunAsync(runConfiguration, configuration).ConfigureAwait(false);

            var explicitPaths = ParseExplicitPaths(runConfiguration);
            var isFullInventory = explicitPaths == null;

            if (isFullInventory)
            {
                if (!runConfiguration.RebuildReport)
                {
                    var cachedReport = await GitWizardReport.GetCachedReportAsync(cancellationToken).ConfigureAwait(false);
                    if (!runConfiguration.RefreshReport && cachedReport != null)
                    {
                        await using (cachedReport)
                        {
                            var cachedRun = cachedReport.Run ?? new ReportRunSummary();
                            cachedReport.Run = cachedRun with
                            {
                                Warnings = [.. cachedRun.Warnings, .. updateHandler.CaptureRun().Warnings]
                            };
                            return OutputReport(cachedReport, runConfiguration, stdout, fromCache: true);
                        }
                    }
                }

                if (!EnsureUsableSearchPaths(configuration, _ => { }))
                    return OutputFailedRun(updateHandler.CaptureRun(), runConfiguration, stdout, 1);
            }

            var report = await GenerateReportForRunAsync(
                runConfiguration, configuration, explicitPaths, updateHandler, coordinator).ConfigureAwait(false);

            await using (report)
            {
                var exitCode = GetDiscoveryExitCode(report.DiscoveryInventory, isFullInventory);
                if (exitCode is { } failureExitCode)
                {
                    if (report.DiscoveryInventory is not null)
                        RecordDiscoveryDiagnostics(report.DiscoveryInventory, updateHandler);
                    return OutputFailedRun(updateHandler.CaptureRun(), runConfiguration, stdout, failureExitCode);
                }

                report.Run = updateHandler.CaptureRun();
                await SaveReportAsync(runConfiguration, report).ConfigureAwait(false);
                report.Run = updateHandler.CaptureRun();

                return OutputReport(report, runConfiguration, stdout);
            }
        }
        finally
        {
            GitWizardLog.LogMethod = originalLogMethod;
        }
    }

    static Task<GitWizardReport> GenerateReportForRunAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        string[]? explicitPaths,
        IUpdateHandler updateHandler,
        RepositoryDiscoveryCoordinator? coordinator)
    {
        if (explicitPaths == null)
        {
            var reportOptions = new GitWizardReportOptions
            {
                NoMft = runConfiguration.NoMft,
                AllBranches = runConfiguration.AllBranches,
                ComputeLocalCommitCount = !runConfiguration.NoLocalCommitCount,
                DiscoveryCoordinator = coordinator,
                DiscoveryPurpose = runConfiguration.RebuildRepositoryList ? DiscoveryPurpose.Rebuild : DiscoveryPurpose.Ordinary,
                AllowRepositoryList = !runConfiguration.RebuildRepositoryList
            };

            return GitWizardReport.GenerateReportAsync(configuration, null, updateHandler, reportOptions);
        }

        var explicitOptions = new GitWizardReportOptions
        {
            NoMft = runConfiguration.NoMft,
            AllBranches = runConfiguration.AllBranches,
            ComputeLocalCommitCount = !runConfiguration.NoLocalCommitCount
        };
        return GitWizardReport.GenerateReportAsync(configuration, explicitPaths, updateHandler, explicitOptions);
    }

    static void RecordDiscoveryDiagnostics(DiscoveryInventory inventory, UpdateHandler updateHandler)
    {
        using var diagnostics = new StringWriter();
        WriteDiscoveryDiagnostics(inventory, diagnostics);
        foreach (var line in diagnostics.ToString().Split(
            Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            updateHandler.RecordWarning(line);
    }

    internal static int OutputFailedRun(ReportRunSummary run, RunConfiguration configuration,
        TextWriter stdout, int exitCode)
    {
        if (!configuration.Silent && !GitWizardLog.SilentMode)
        {
            if (configuration.Json)
                stdout.WriteLine(JsonSerializer.Serialize(new { run },
                    GetSerializerOptions(configuration, JsonIgnoreCondition.WhenWritingDefault)));
            else
                ReportTextFormatter.WriteRun(stdout, run);
        }
        return exitCode;
    }

    internal static int OutputReport(GitWizardReport report, RunConfiguration runConfiguration, TextWriter stdout,
        bool fromCache = false)
    {
        report.Run ??= new ReportRunSummary();
        var filteredReport = ApplyFilter(runConfiguration, report);
        if (!GitWizardLog.SilentMode && !runConfiguration.Silent)
        {
            if (runConfiguration.Json)
                stdout.WriteLine(SerializeReport(runConfiguration, filteredReport));
            else
                ReportTextFormatter.Write(stdout, filteredReport, fromCache);
        }
        return ReportNeedsAttention(filteredReport) ? 1 : 0;
    }

    /// <summary>
    /// Handle the --merge flag: validate required args, refresh the supplied repos, and merge
    /// them into the existing report at --save-path (atomic write, other entries preserved).
    /// </summary>
    static void RunMerge(RunConfiguration runConfiguration, GitWizardConfiguration configuration, TextWriter stderr)
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
        var updateHandler = new UpdateHandler(stderr);
        GitWizardReport.MergeIntoFile(savePath, configuration, explicitPaths, updateHandler,
            allBranches: runConfiguration.AllBranches, computeLocalCommitCount: !runConfiguration.NoLocalCommitCount);
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
        var options = GetSerializerOptions(runConfiguration, JsonIgnoreCondition.WhenWritingDefault);

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
            IncludeTempDirectories = report.IncludeTempDirectories,
            Run = report.Run,
            BranchScope = report.BranchScope,
            SkipHiddenDirectories = report.SkipHiddenDirectories
        };

        foreach (var kvp in report.Repositories)
        {
            if (MatchesFilter(kvp.Key, runConfiguration.FilterPattern))
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
