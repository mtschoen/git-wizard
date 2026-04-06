using System.Text.Json;
using System.Text.Json.Serialization;

// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

public static class Program
{
    /// <summary>
    /// Struct containing configuration details specified in command line arguments
    /// TODO: use a library for parsing CLI args that can also generate a -h manual
    /// </summary>
    struct RunConfiguration
    {
        const string k_HelpManual = @"GitWizard 0.4.0 Help
Supported command line arguments (all optional):
  -v                        Enable verbose logging.
  -silent                   Do not print to the console
  -rebuild-report           Rebuild the list of repositories (instead of using cache)
  -rebuild-repo-list        Rebuild the report from scratch (instead of using cache)
  -no-refresh               Do not refresh the report based on the latest state; just print out the cached report
  -rebuild-all              Rebuild the report and list of repositories (instead of using cache)
  -print-minified           Print the output to the console as minified JSON
  -save-path                Path where the report will be saved to disk (otherwise it is only printed to the console)
  -config-path              Path to custom configuration file (otherwise global or default configuration is used)
  -clear-cache              Delete cached reports and configurations before running
                            (combine with -no-refresh to avoid re-generating the cache)
  -delete-all-local-files   Delete all files created by GitWizard before running (includes files deleted by -clear-cache;
                            combine with -no-refresh to avoid creating any more local files)
  -setup-defender           Add Windows Defender exclusions for git/dotnet processes and search paths (triggers UAC prompt)
  -scan-only                Print discovered repository paths (one per line) and exit without refreshing
  -no-mft                   Skip MFT search and use recursive directory scan instead
  -filter <pattern>         Filter output to repositories whose path contains <pattern> (case-insensitive)
  -paths <file-or-csv>      Report on specific repo paths (newline-separated file or comma-separated list)
  -summary                  Output a condensed summary (dirty/unpushed/stale counts + repos needing attention)
";

        /// <summary>
        /// Rebuild the list of repositories (instead of using cache).
        /// </summary>
        public readonly bool RebuildRepositoryList = false;

        /// <summary>
        /// Rebuild the report from scratch (instead of using cache).
        /// </summary>
        public readonly bool RebuildReport = false;

        /// <summary>
        /// Delete cached reports and configurations before doing anything else.
        /// </summary>
        public readonly bool ClearCache = false;

        /// <summary>
        /// Delete all local files before doing anything else.
        /// </summary>
        public readonly bool DeleteAllLocalFiles = false;

        /// <summary>
        /// Add Windows Defender exclusions before running.
        /// </summary>
        public readonly bool SetupDefender = false;

        /// <summary>
        /// Print discovered repository paths and exit without refreshing.
        /// </summary>
        public readonly bool ScanOnly = false;

        /// <summary>
        /// Skip MFT search and use recursive directory scan instead.
        /// </summary>
        public readonly bool NoMft = false;

        /// <summary>
        /// Case-insensitive substring filter applied to repository paths in the output.
        /// </summary>
        public readonly string? FilterPattern = null;

        /// <summary>
        /// Explicit list of repository paths to report on, bypassing discovery.
        /// Can be a file path (newline-separated) or comma-separated inline list.
        /// </summary>
        public readonly string? PathsArgument = null;

        /// <summary>
        /// Output a condensed summary instead of the full report.
        /// </summary>
        public readonly bool Summary = false;

        /// <summary>
        /// Refresh the report based on the latest state (otherwise just print out the cached report).
        /// </summary>
        public readonly bool RefreshReport = true;

        /// <summary>
        /// Print/save the report as minified JSON.
        /// </summary>
        public readonly bool Minified = false;

        /// <summary>
        /// Path where the report will be saved to disk (otherwise it is only printed to the console).
        /// </summary>
        public readonly string? SavePath = null;

        /// <summary>
        /// Path to custom configuration file (otherwise global or default configuration is used)
        /// </summary>
        public readonly string? CustomConfigurationPath = null;

        /// <summary>
        /// Initialize a RunConfiguration using Environment.GetCommandLineArgs
        /// </summary>
        public RunConfiguration()
        {
            var arguments = Environment.GetCommandLineArgs();
            var length = arguments.Length;
            for (var i = 0; i < length; i++)
            {
                var argument = arguments[i];
                switch (argument)
                {
                    case "-h":
                        Console.WriteLine(k_HelpManual);
                        Environment.Exit(0);
                        break;
                    case "-v":
                        GitWizardLog.VerboseMode = true;
                        break;
                    case "-silent":
                        GitWizardLog.SilentMode = true;
                        break;
                    case "-rebuild-report":
                        RebuildReport = true;
                        break;
                    case "-no-refresh":
                        RefreshReport = false;
                        break;
                    case "-rebuild-repo-list":
                        RebuildRepositoryList = true;
                        break;
                    case "-rebuild-all":
                        RebuildReport = true;
                        RebuildRepositoryList = true;
                        break;
                    case "-setup-defender":
                        SetupDefender = true;
                        break;
                    case "-scan-only":
                        ScanOnly = true;
                        RebuildRepositoryList = true;
                        break;
                    case "-no-mft":
                        NoMft = true;
                        break;
                    case "-filter":
                        if (i + 1 >= length)
                        {
                            GitWizardLog.Log("-filter argument passed without a following argument.", GitWizardLog.LogType.Error);
                            break;
                        }

                        FilterPattern = arguments[++i];
                        break;
                    case "-paths":
                        if (i + 1 >= length)
                        {
                            GitWizardLog.Log("-paths argument passed without a following argument.", GitWizardLog.LogType.Error);
                            break;
                        }

                        PathsArgument = arguments[++i];
                        break;
                    case "-summary":
                        Summary = true;
                        break;
                    case "-clear-cache":
                        ClearCache = true;
                        break;
                    case "-delete-all-local-files":
                        DeleteAllLocalFiles = true;
                        break;
                    case "-print-minified":
                        Minified = true;
                        break;
                    case "-save-path":
                        if (i >= length)
                        {
                            GitWizardLog.Log("-save-path argument passed without a following argument.", GitWizardLog.LogType.Error);
                            break;
                        }

                        // TODO: Validate path
                        SavePath = arguments[i + 1];
                        break;
                    case "-config-path":
                        if (i >= length)
                        {
                            GitWizardLog.Log("-config-path argument passed without a following argument.", GitWizardLog.LogType.Error);
                            break;
                        }

                        // TODO: Validate path
                        CustomConfigurationPath = arguments[i + 1];
                        break;
                }
            }
        }
    }

    const string k_SessionStartMessage = @"Session Start Message
=======================================================================================================================
git-wizard Session Started
=======================================================================================================================";

    public static void Main()
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
                        GitWizardApi.RunElevatedMftScan(configPath, outputPath);
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
                    var success = WindowsDefenderException.RunDefenderCommands();
                    Environment.Exit(success ? 0 : 1);
                    return;
                }
            }
        }

        var runConfiguration = new RunConfiguration();
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

        GitWizardLog.Log(k_SessionStartMessage);
        var configuration = GetConfiguration(runConfiguration);

        if (runConfiguration.SetupDefender)
        {
            GitWizardLog.Log("Setting up Windows Defender exclusions...");
            WindowsDefenderException.AddExclusions();
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

        var repositoryPaths = GetRepositoryPaths(runConfiguration) ?? ParseExplicitPaths(runConfiguration);
        var updateHandler = new UpdateHandler();
        var report = GetReport(runConfiguration, configuration, repositoryPaths, updateHandler);
        if (report == null)
        {
            // If the user requested to not generate a new report, and no cached report exists, early out
            GitWizardLog.Log("Could not retrieve cached report", GitWizardLog.LogType.Error);
            Environment.Exit(0);
            return;
        }

        // Process any queued commands
        GitWizardLog.Log("Processing queued commands...");
        updateHandler.ProcessCommands();
        updateHandler.PrintSummary();

        if (repositoryPaths == null)
            GitWizardApi.SaveCachedRepositoryPaths(report.GetRepositoryPaths());

        SaveReport(runConfiguration, report);

        var filteredReport = ApplyFilter(runConfiguration, report);
        var needsAttention = ReportNeedsAttention(filteredReport);

        string jsonString;
        if (runConfiguration.Summary)
        {
            var summary = GitWizardSummary.FromReport(filteredReport);
            var options = new JsonSerializerOptions
            {
                WriteIndented = !runConfiguration.Minified,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
            };
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
        var options = new JsonSerializerOptions
        {
            WriteIndented = !runConfiguration.Minified,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
        };

        var jsonString = JsonSerializer.Serialize(report, options);
        return jsonString;
    }

    static GitWizardReport? GetReport(RunConfiguration runConfiguration, GitWizardConfiguration configuration,
        ICollection<string>? repositoryPaths, UpdateHandler updateHandler)
    {
        if (!runConfiguration.RebuildReport)
        {
            var cachedReport = GitWizardReport.GetCachedReport();
            if (!runConfiguration.RefreshReport)
                return cachedReport;

            if (cachedReport != null && repositoryPaths != null)
            {
                cachedReport.Refresh(repositoryPaths, updateHandler);
                return cachedReport;
            }
        }

        return GitWizardReport.GenerateReport(configuration, repositoryPaths, updateHandler,
            noMft: runConfiguration.NoMft);
    }

    static string[]? GetRepositoryPaths(RunConfiguration runConfiguration)
    {
        return runConfiguration.RebuildRepositoryList ? null : GitWizardApi.GetCachedRepositoryPaths();
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

    static GitWizardConfiguration GetConfiguration(RunConfiguration runConfiguration)
    {
        var customConfigurationPath = runConfiguration.CustomConfigurationPath;
        if (customConfigurationPath == null)
            return GitWizardConfiguration.GetGlobalConfiguration();

        var configuration = GitWizardConfiguration.GetConfigurationAtPath(customConfigurationPath);
        if (configuration != null)
            return configuration;

        GitWizardLog.Log($"Could not find custom configuration at path: {customConfigurationPath}", GitWizardLog.LogType.Error);
        Environment.Exit(0);
        return null;
    }
}
