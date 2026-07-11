// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

public static partial class Program
{
    /// <summary>
    /// Struct containing configuration details specified in command line arguments
    /// </summary>
    struct RunConfiguration
    {
        const string HelpManual = @"GitWizard 0.4.1 Help

Usage: git-wizard [options]

Key options:
  -filter <pattern>         Filter output to repositories whose path contains <pattern> (case-insensitive)
  -paths <file-or-csv>      Report on specific repo paths (newline-separated file or comma-separated list)
  -merge                    Targeted single-repo refresh: merge the repos named by -paths into the existing
                            report at -save-path (insert/update those entries, leave all others intact),
                            then write back atomically. Requires -paths and -save-path.
  -summary                  Output a condensed summary (dirty/unpushed/stale counts + repos needing attention)

Other options:
  -h, --help, -?            Print this help message and exit
  -v                        Enable verbose logging.
  -silent                   Do not print to the console
  -rebuild-report           Rebuild the list of repositories (instead of using cache)
  -rebuild-repo-list        Rebuild the report from scratch (instead of using cache)
  -no-refresh               Do not refresh the report based on the latest state; just print out the cached report
  -rebuild-all              Rebuild the report and list of repositories (instead of using cache)
  -print-minified           Print the output to the console as minified JSON
  -save-path <path>         Path where the report will be saved to disk (otherwise it is only printed to the console)
  -config-path <path>       Path to custom configuration file (otherwise global or default configuration is used)
  -clear-cache              Delete cached reports and configurations before running
                            (combine with -no-refresh to avoid re-generating the cache)
  -delete-all-local-files   Delete all files created by GitWizard before running (includes files deleted by -clear-cache;
                            combine with -no-refresh to avoid creating any more local files)
  -setup-defender           Add Windows Defender exclusions for git/dotnet processes and search paths (triggers UAC prompt)
  -scan-only                Print discovered repository paths (one per line) and exit without refreshing
  -no-mft                   Skip MFT search and use recursive directory scan instead
  -db-size                  Show the size of the GitWizard local files folder (~/.GitWizard/) and exit
  -all-branches             Include all local branches (default + already-merged) in each repo's Branches list, not just actionable ones
  -watch                    Watch tracked repositories for changes via MFTLib's USN journal broker
                            (Windows only; triggers one UAC prompt) and print a line per repo that
                            changes. Runs until Ctrl-C.
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
        /// Targeted single-repo merge refresh: merge the repos named by -paths into the
        /// existing report at -save-path, leaving other entries intact. Requires -paths
        /// and -save-path.
        /// </summary>
        public readonly bool Merge = false;

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
        /// Show the size of the GitWizard local files folder and exit.
        /// </summary>
        public readonly bool DbSize = false;

        /// <summary>
        /// Include all branches (default + boring) in the per-repo Branches list,
        /// not just actionable branches.
        /// </summary>
        public readonly bool AllBranches = false;

        /// <summary>
        /// Watch tracked repositories for changes via MFTLib's USN journal broker
        /// (Windows only) and print a line per repo that changes.
        /// </summary>
        public readonly bool Watch = false;

        /// <summary>
        /// Initialize a RunConfiguration using Environment.GetCommandLineArgs
        /// </summary>
        public RunConfiguration()
        {
            var parsed = ParseCommandLine(Environment.GetCommandLineArgs());
            RebuildRepositoryList = parsed.RebuildRepositoryList;
            RebuildReport = parsed.RebuildReport;
            ClearCache = parsed.ClearCache;
            DeleteAllLocalFiles = parsed.DeleteAllLocalFiles;
            SetupDefender = parsed.SetupDefender;
            ScanOnly = parsed.ScanOnly;
            NoMft = parsed.NoMft;
            FilterPattern = parsed.FilterPattern;
            PathsArgument = parsed.PathsArgument;
            Summary = parsed.Summary;
            Merge = parsed.Merge;
            RefreshReport = parsed.RefreshReport;
            Minified = parsed.Minified;
            SavePath = parsed.SavePath;
            CustomConfigurationPath = parsed.CustomConfigurationPath;
            DbSize = parsed.DbSize;
            AllBranches = parsed.AllBranches;
            Watch = parsed.Watch;
        }

        static ParsedArguments ParseCommandLine(string[] arguments)
        {
            var parsed = new ParsedArguments();
            var length = arguments.Length;
            for (var i = 0; i < length; i++)
            {
                var argument = arguments[i];
                if (TryApplySimpleFlag(parsed, argument))
                    continue;

                switch (argument)
                {
                    case "-filter":
                        if (i + 1 >= length)
                        {
                            GitWizardLog.Log("-filter argument passed without a following argument.", GitWizardLog.LogType.Error);
                            break;
                        }

                        parsed.FilterPattern = arguments[++i];
                        break;
                    case "-paths":
                        if (i + 1 >= length)
                        {
                            GitWizardLog.Log("-paths argument passed without a following argument.", GitWizardLog.LogType.Error);
                            break;
                        }

                        parsed.PathsArgument = arguments[++i];
                        break;
                    case "-save-path":
                        if (i >= length)
                        {
                            GitWizardLog.Log("-save-path argument passed without a following argument.", GitWizardLog.LogType.Error);
                            break;
                        }

                        parsed.SavePath = arguments[i + 1];
                        break;
                    case "-config-path":
                        if (i >= length)
                        {
                            GitWizardLog.Log("-config-path argument passed without a following argument.", GitWizardLog.LogType.Error);
                            break;
                        }

                        parsed.CustomConfigurationPath = arguments[i + 1];
                        break;
                }
            }

            return parsed;
        }

        static bool TryApplySimpleFlag(ParsedArguments parsed, string argument)
        {
            switch (argument)
            {
                case "-h":
                case "--help":
                case "-?":
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
                    parsed.RebuildReport = true;
                    break;
                case "-no-refresh":
                    parsed.RefreshReport = false;
                    break;
                case "-rebuild-repo-list":
                    parsed.RebuildRepositoryList = true;
                    break;
                case "-rebuild-all":
                    parsed.RebuildReport = true;
                    parsed.RebuildRepositoryList = true;
                    break;
                case "-setup-defender":
                    parsed.SetupDefender = true;
                    break;
                case "-scan-only":
                    parsed.ScanOnly = true;
                    parsed.RebuildRepositoryList = true;
                    break;
                case "-no-mft":
                    parsed.NoMft = true;
                    break;
                case "-summary":
                    parsed.Summary = true;
                    break;
                case "-merge":
                    parsed.Merge = true;
                    break;
                case "-clear-cache":
                    parsed.ClearCache = true;
                    break;
                case "-delete-all-local-files":
                    parsed.DeleteAllLocalFiles = true;
                    break;
                case "-print-minified":
                    parsed.Minified = true;
                    break;
                case "-db-size":
                    parsed.DbSize = true;
                    break;
                case "-all-branches":
                    parsed.AllBranches = true;
                    break;
                case "-watch":
                    parsed.Watch = true;
                    break;
                default:
                    return false;
            }

            return true;
        }

        sealed class ParsedArguments
        {
            public bool RebuildRepositoryList { get; set; }
            public bool RebuildReport { get; set; }
            public bool ClearCache { get; set; }
            public bool DeleteAllLocalFiles { get; set; }
            public bool SetupDefender { get; set; }
            public bool ScanOnly { get; set; }
            public bool NoMft { get; set; }
            public string? FilterPattern { get; set; }
            public string? PathsArgument { get; set; }
            public bool Summary { get; set; }
            public bool Merge { get; set; }
            public bool RefreshReport { get; set; } = true;
            public bool Minified { get; set; }
            public string? SavePath { get; set; }
            public string? CustomConfigurationPath { get; set; }
            public bool DbSize { get; set; }
            public bool AllBranches { get; set; }
            public bool Watch { get; set; }
        }
    }
}
