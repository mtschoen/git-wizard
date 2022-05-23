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
        const string HelpManual = @"GitWizard 0.1 Help
Supported command line arguments (all optional):
  -v                    Enable verbose logging.
  -silent               Do not print to the console
  -rebuild-report       Rebuild the list of repositories (instead of using cache)
  -rebuild-repo-list    Rebuild the report from scratch (instead of using cache)
  -no-refresh           Do not refresh the report based on the latest state; just print out the cached report
  -rebuild-all          Rebuild the report and list of repositories (instead of using cache)
  -print-minified       Print the output to the console as minified JSON
  -save-path            Path where the report will be saved to disk (otherwise it is only printed to the console)
  -config-path          Path to custom configuration file (otherwise global or default configuration is used)
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
                        Console.WriteLine(HelpManual);
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

    public static async Task Main()
    {
        var runConfiguration = new RunConfiguration();
        var configuration = GetConfiguration(runConfiguration);
        var repositoryPaths = GetRepositoryPaths(runConfiguration);
        var report = await GetReport(runConfiguration, configuration, repositoryPaths);
        if (report == null)
        {
            // If the user requested to not generate a new report, and no cached report exists, early out
            GitWizardLog.Log("Could not retrieve cached report", GitWizardLog.LogType.Error);
            Environment.Exit(0);
            return;
        }

        if (repositoryPaths == null)
            GitWizardApi.SaveCachedRepositoryPaths(report.GetRepositoryPaths());

        SaveReport(runConfiguration, report);

        var jsonString = SerializeReport(runConfiguration, report);

        if (!GitWizardLog.SilentMode)
            Console.WriteLine(jsonString);
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

    static async Task<GitWizardReport?> GetReport(RunConfiguration runConfiguration, GitWizardConfiguration configuration,
        ICollection<string>? repositoryPaths = null)
    {
        if (!runConfiguration.RebuildReport)
        {
            var cachedReport = GitWizardReport.GetCachedReport();
            if (!runConfiguration.RefreshReport)
                return cachedReport;

            if (cachedReport != null && repositoryPaths != null)
            {
                await cachedReport.Refresh(repositoryPaths);
                return cachedReport;
            }
        }

        return await GitWizardReport.GenerateReport(configuration, repositoryPaths,
            path => GitWizardLog.Log(path, GitWizardLog.LogType.Verbose));
    }

    static string[]? GetRepositoryPaths(RunConfiguration runConfiguration)
    {
        return runConfiguration.RebuildRepositoryList ? null : GitWizardApi.GetCachedRepositoryPaths();
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
