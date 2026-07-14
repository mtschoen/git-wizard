using System.Text.Json;
using System.Text.Json.Serialization;
using MFTLib;

// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

public static partial class Program
{
    // Cached serializer options (CA1869): WriteIndented depends on the --print-minified flag,
    // so two presets are kept rather than allocating a JsonSerializerOptions per call.
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

    const string SessionStartMessage = @"Session Start Message
=======================================================================================================================
git-wizard Session Started
=======================================================================================================================";

    public static async Task Main()
    {
        var args = Environment.GetCommandLineArgs();

        // Dispatch MFTLib's elevated journal-broker child mode (launched by --watch's
        // self-elevation) before any other startup work.
        if (TryHandleElevatedBrokerEntry(args, new DefaultElevatedEntryRunner()))
            return;

        if (TryHandleElevatedHelperModes(args))
            return;

        var runConfiguration = new RunConfiguration();

        // --help/--version already printed their text via McMaster; stop here rather than
        // falling through to a full report run (they carry no other flags to distinguish
        // them from a bare invocation).
        if (runConfiguration.ExitRequested)
            return;

        if (runConfiguration.HasParseError)
        {
            GitWizardLog.Log("Aborting due to CLI parsing errors.", GitWizardLog.LogType.Error);
            Environment.Exit(2);
            return;
        }

        if (TryHandleImmediateExitFlags(runConfiguration))
            return;

        GitWizardLog.Log(SessionStartMessage);
        var configuration = await GetConfigurationAsync(runConfiguration);

        if (runConfiguration.SetupDefender)
        {
            GitWizardLog.Log("Setting up Windows Defender exclusions...");
            WindowsDefender.AddExclusions();
        }

        if (runConfiguration.Merge)
        {
            RunMerge(runConfiguration, configuration);
            Environment.Exit(0);
            return;
        }

        if (runConfiguration.ScanOnly)
        {
            await RunScanOnlyAsync(runConfiguration, configuration);
            return;
        }

        if (runConfiguration.Watch)
        {
            if (!OperatingSystem.IsWindows())
            {
                GitWizardLog.Log("--watch requires Windows.", GitWizardLog.LogType.Error);
                Environment.Exit(1);
                return;
            }

            await RunWatchAsync(configuration);
            Environment.Exit(0);
            return;
        }

        await RunDefaultReportModeAsync(runConfiguration, configuration);
    }

    // Handle elevated helper modes (launched by self-elevation). Returns true if one of the
    // helper-mode args was found and handled, meaning the caller should return immediately.
    static bool TryHandleElevatedHelperModes(string[] args)
    {
        if (!args.Contains("--elevated-defender"))
            return false;

        Environment.Exit(WindowsDefender.RunDefenderCommands() ? 0 : 1);
        return true;
    }

    // Handle the flags that resolve to an immediate exit before any report is generated
    // (--db-size, --delete-all-local-files, --clear-cache). Returns true if Main should return
    // immediately; false if execution should continue into report generation.
    static bool TryHandleImmediateExitFlags(RunConfiguration runConfiguration)
    {
        if (runConfiguration.DbSize)
        {
            var sizeBytes = GitWizardApi.GetLocalFilesSize();
            Console.WriteLine(FormatSize(sizeBytes));
            Environment.Exit(0);
        }

        if (runConfiguration.DeleteAllLocalFiles)
        {
            GitWizardApi.DeleteAllLocalFiles();
            if (!runConfiguration.RefreshReport)
            {
                Environment.Exit(0);
                return true;
            }
        }

        if (runConfiguration.ClearCache)
        {
            GitWizardApi.ClearCache();
            if (!runConfiguration.RefreshReport)
            {
                Environment.Exit(0);
                return true;
            }
        }

        return false;
    }

    static async Task RunScanOnlyAsync(RunConfiguration runConfiguration, GitWizardConfiguration configuration)
    {
        var scanReport = new GitWizardReport(configuration);
        var scanPaths = new SortedSet<string>();
        await scanReport.GetRepositoryPathsAsync(scanPaths, noMft: runConfiguration.NoMft);
        foreach (var path in scanPaths)
        {
            Console.WriteLine(path);
        }

        Environment.Exit(0);
    }

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

        SaveReport(runConfiguration, report);

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

    static async Task<GitWizardConfiguration> GetConfigurationAsync(RunConfiguration runConfiguration)
    {
        var customConfigurationPath = runConfiguration.CustomConfigurationPath;
        if (customConfigurationPath == null)
            return await GitWizardConfiguration.GetGlobalConfigurationAsync().ConfigureAwait(false);

        var configuration = await GitWizardConfiguration.GetConfigurationAtPathAsync(customConfigurationPath).ConfigureAwait(false);
        if (configuration != null)
            return configuration;

        GitWizardLog.Log($"Could not find custom configuration at path: {customConfigurationPath}", GitWizardLog.LogType.Error);
        Environment.Exit(1);
        throw new InvalidOperationException("unreachable");
    }

    static async Task<string[]?> GetRepositoryPathsAsync(RunConfiguration runConfiguration)
    {
        return runConfiguration.RebuildRepositoryList ? null : await GitWizardApi.GetCachedRepositoryPathsAsync().ConfigureAwait(false);
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

        return await GitWizardReport.GenerateReportAsync(configuration, repositoryPaths, updateHandler,
            new GitWizardReportOptions { NoMft = runConfiguration.NoMft, AllBranches = runConfiguration.AllBranches, ComputeLocalCommitCount = !runConfiguration.NoLocalCommitCount });
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

    static void SaveReport(RunConfiguration runConfiguration, GitWizardReport report)
    {
        // Always save a cache
        report.Save(GitWizardReport.GetCachedReportPath());

        var savePath = runConfiguration.SavePath;
        if (savePath == null)
            return;

        GitWizardLog.Log($"Saving report to {savePath}");

        report.Save(savePath);
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
            IgnoredPaths = report.IgnoredPaths
        };

        foreach (var kvp in report.Repositories)
        {
            if (kvp.Key.Contains(runConfiguration.FilterPattern, StringComparison.OrdinalIgnoreCase))
                filtered.Repositories[kvp.Key] = kvp.Value;
        }

        return filtered;
    }

    static string[]? ParseExplicitPaths(RunConfiguration runConfiguration)
    {
        var argument = runConfiguration.PathsArgument;
        if (string.IsNullOrEmpty(argument))
            return null;

        // If it's a file, read lines from it
        if (File.Exists(argument))
        {
            return File.ReadAllLines(argument)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToArray();
        }

        // Otherwise treat as comma-separated
        return argument.Split(',')
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToArray();
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
