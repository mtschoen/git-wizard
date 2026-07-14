// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

public static partial class Program
{
    /// <summary>
    /// Struct containing configuration details specified in command line arguments
    /// </summary>
    struct RunConfiguration
    {
        /// <summary>
        /// Rebuild the list of repositories (instead of using cache).
        /// </summary>
        public readonly bool RebuildRepositoryList;

        /// <summary>
        /// Rebuild the report from scratch (instead of using cache).
        /// </summary>
        public readonly bool RebuildReport;

        /// <summary>
        /// Delete cached reports and configurations before doing anything else.
        /// </summary>
        public readonly bool ClearCache;

        /// <summary>
        /// Delete all local files before doing anything else.
        /// </summary>
        public readonly bool DeleteAllLocalFiles;

        /// <summary>
        /// Add Windows Defender exclusions before running.
        /// </summary>
        public readonly bool SetupDefender;

        /// <summary>
        /// Print discovered repository paths and exit without refreshing.
        /// </summary>
        public readonly bool ScanOnly;

        /// <summary>
        /// Skip MFT search and use recursive directory scan instead.
        /// </summary>
        public readonly bool NoMft;

        /// <summary>
        /// Case-insensitive substring filter applied to repository paths in the output.
        /// </summary>
        public readonly string? FilterPattern;

        /// <summary>
        /// Explicit list of repository paths to report on, bypassing discovery.
        /// Can be a file path (newline-separated) or comma-separated inline list.
        /// </summary>
        public readonly string? PathsArgument;

        /// <summary>
        /// Output a condensed summary instead of the full report.
        /// </summary>
        public readonly bool Summary;

        /// <summary>
        /// Targeted single-repo merge refresh: merge the repos named by --paths into the
        /// existing report at --save-path, leaving other entries intact. Requires --paths
        /// and --save-path.
        /// </summary>
        public readonly bool Merge;

        /// <summary>
        /// Refresh the report based on the latest state (otherwise just print out the cached report).
        /// </summary>
        public readonly bool RefreshReport;

        /// <summary>
        /// Print/save the report as minified JSON.
        /// </summary>
        public readonly bool Minified;

        /// <summary>
        /// Path where the report will be saved to disk (otherwise it is only printed to the console).
        /// </summary>
        public readonly string? SavePath;

        /// <summary>
        /// Path to custom configuration file (otherwise global or default configuration is used)
        /// </summary>
        public readonly string? CustomConfigurationPath;

        /// <summary>
        /// Show the size of the GitWizard local files folder and exit.
        /// </summary>
        public readonly bool DbSize;

        /// <summary>
        /// Include all branches (default + boring) in the per-repo Branches list,
        /// not just actionable branches.
        /// </summary>
        public readonly bool AllBranches;

        /// <summary>
        /// Watch tracked repositories for changes via MFTLib's USN journal broker
        /// (Windows only) and print a line per repo that changes.
        /// </summary>
        public readonly bool Watch;

        /// <summary>
        /// When true, skip the expensive per-branch local-commit-count iteration
        /// in each repository refresh. Defaults to false (compute counts).
        /// </summary>
        public readonly bool NoLocalCommitCount;

        /// <summary>
        /// True if CLI parsing encountered validation errors (e.g., --save-path with
        /// nonexistent parent directory, --config-path pointing to a missing file).
        /// </summary>
        public readonly bool HasParseError;

        /// <summary>
        /// True when McMaster already printed help or version text (--help, --version) and
        /// the caller should exit immediately without running any report generation.
        /// </summary>
        public readonly bool ExitRequested;

        /// <summary>
        /// Initialize a RunConfiguration using Environment.GetCommandLineArgs
        /// </summary>
        public RunConfiguration()
        {
            var result = CliParser.ParseProcessArgs(Environment.GetCommandLineArgs());
            RebuildRepositoryList = result.RebuildRepositoryList;
            RebuildReport = result.RebuildReport;
            ClearCache = result.ClearCache;
            DeleteAllLocalFiles = result.DeleteAllLocalFiles;
            SetupDefender = result.SetupDefender;
            ScanOnly = result.ScanOnly;
            NoMft = result.NoMft;
            FilterPattern = result.FilterPattern;
            PathsArgument = result.PathsArgument;
            Summary = result.Summary;
            Merge = result.Merge;
            RefreshReport = result.RefreshReport;
            Minified = result.Minified;
            SavePath = result.SavePath;
            CustomConfigurationPath = result.CustomConfigurationPath;
            DbSize = result.DbSize;
            AllBranches = result.AllBranches;
            Watch = result.Watch;
            NoLocalCommitCount = result.NoLocalCommitCount;
            HasParseError = result.HasError;
            ExitRequested = result.ExitRequested;
        }
    }
}
