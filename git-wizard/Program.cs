using System.Text.Json;
using System.Text.Json.Serialization;

// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

public static partial class Program
{
    // Cached serializer options (CA1869): WriteIndented depends on the -minified flag,
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
        // Handle elevated helper modes (launched by self-elevation)
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--elevated-mft":
                    {
                        string? configPath = null;
                        string? outputPath = null;
                        for (var j = i + 1; j < args.Length; j++)
                        {
                            switch (args[j])
                            {
                                case "--config-path":
                                    if (j + 1 < args.Length) configPath = args[++j];
                                    break;
                                case "--output":
                                    if (j + 1 < args.Length) outputPath = args[++j];
                                    break;
                            }
                        }

                        if (configPath != null && outputPath != null)
                        {
                            await Task.Run(() => GitWizardApi.RunElevatedMftScan(configPath, outputPath)).ConfigureAwait(false);
                            Environment.Exit(0);
                        }
                        else
                        {
                            Environment.Exit(1);
                        }

                        return;
                    }
                case "--elevated-defender":
                    {
                        var success = WindowsDefender.RunDefenderCommands();
                        Environment.Exit(success ? 0 : 1);
                        return;
                    }
            }
        }

        var runConfiguration = new RunConfiguration();
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
                return;
            }
        }

        if (runConfiguration.ClearCache)
        {
            GitWizardApi.ClearCache();
            if (!runConfiguration.RefreshReport)
            {
                Environment.Exit(0);
                return;
            }
        }

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
            var scanReport = new GitWizardReport(configuration);
            var scanPaths = new SortedSet<string>();
            scanReport.GetRepositoryPaths(scanPaths, noMft: runConfiguration.NoMft);
            foreach (var path in scanPaths)
            {
                Console.WriteLine(path);
            }

            Environment.Exit(0);
            return;
        }

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
                cachedReport.Refresh(repositoryPaths, updateHandler, allBranches: runConfiguration.AllBranches);
                cachedReport.BranchScope = runConfiguration.AllBranches ? "all" : "actionable";
                return cachedReport;
            }
        }

        return GitWizardReport.GenerateReport(configuration, repositoryPaths, updateHandler,
            noMft: runConfiguration.NoMft, allBranches: runConfiguration.AllBranches);
    }

    /// <summary>
    /// Handle the -merge flag: validate required args, refresh the supplied repos, and merge
    /// them into the existing report at -save-path (atomic write, other entries preserved).
    /// </summary>
    static void RunMerge(RunConfiguration runConfiguration, GitWizardConfiguration configuration)
    {
        var savePath = runConfiguration.SavePath;
        var explicitPaths = ParseExplicitPaths(runConfiguration);

        if (explicitPaths == null || explicitPaths.Length == 0 || string.IsNullOrEmpty(savePath))
        {
            GitWizardLog.Log(
                "-merge requires both -paths (repos to refresh) and -save-path (report to merge into).",
                GitWizardLog.LogType.Error);
            Environment.Exit(2);
            return;
        }

        GitWizardLog.Log($"Merging {explicitPaths.Length} repo(s) into {savePath}");
        var updateHandler = new UpdateHandler();
        GitWizardReport.MergeIntoFile(savePath, configuration, explicitPaths, updateHandler,
            allBranches: runConfiguration.AllBranches);
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
            if (kvp.Value.HasPendingChanges || kvp.Value.LocalOnlyCommits)
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
