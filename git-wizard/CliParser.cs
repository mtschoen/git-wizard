// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

using McMaster.Extensions.CommandLineUtils;

static class CliParser
{
    /// <summary>
    /// Parses the process's raw command-line args (as returned by
    /// <see cref="Environment.GetCommandLineArgs"/>, which include the program path as
    /// element 0). Strips that leading token before handing off to <see cref="Parse"/>,
    /// which expects a program-name-excluded argument array (the same contract as
    /// <c>Main(string[] args)</c>).
    /// </summary>
    internal static RunConfigurationResult ParseProcessArgs(string[] processArgs) =>
        Parse(processArgs.Length > 0 ? processArgs[1..] : processArgs);

    public static RunConfigurationResult Parse(string[] args)
    {
        using var app = new CommandLineApplication();
        app.Name = "git-wizard";
        app.Description = "Multi-project solution for scanning and reporting on git repositories.";

        var helpOption = app.HelpOption("-h|-?|--help");
        var versionOption = app.VersionOption("--version", "0.4.1");

        var (
            verboseOption,
            silentOption,
            rebuildReportOption,
            noRefreshOption,
            rebuildRepoListOption,
            rebuildAllOption,
            setupDefenderOption,
            scanOnlyOption,
            noMftOption,
            noLocalCommitCountOption,
            summaryOption,
            mergeOption,
            clearCacheOption,
            deleteAllLocalFilesOption,
            printMinifiedOption,
            dbSizeOption,
            allBranchesOption,
            watchOption,
            filterOption,
            pathsOption,
            savePathOption,
            configPathOption
        ) = SetupOptions(app);

        RunConfigurationResult? lastResult = null;
        app.OnExecute(() => lastResult = ExecuteHandler(
            verboseOption,
            silentOption,
            rebuildReportOption,
            noRefreshOption,
            rebuildRepoListOption,
            rebuildAllOption,
            setupDefenderOption,
            scanOnlyOption,
            noMftOption,
            noLocalCommitCountOption,
            summaryOption,
            mergeOption,
            clearCacheOption,
            deleteAllLocalFilesOption,
            printMinifiedOption,
            dbSizeOption,
            allBranchesOption,
            watchOption,
            filterOption,
            pathsOption,
            savePathOption,
            configPathOption));

        try
        {
            app.Execute(args);
        }
        catch (CommandParsingException ex)
        {
            // Parsing failed (e.g., unknown tokens like testhost.dll paths in tests);
            // return default configuration rather than throwing.
            GitWizardLog.Log($"CLI parse failed, using defaults: {ex.Message}", GitWizardLog.LogType.Verbose);
        }

        // McMaster prints the help/version text itself and skips OnExecute, but that leaves
        // no signal in the returned result - without this, the caller can't tell "--help" or
        // "--version" apart from "no args at all" and falls through to a full run.
        if (helpOption.HasValue() || versionOption.HasValue())
            return CreateDefaultResult() with { ExitRequested = true };

        return lastResult ?? CreateDefaultResult();
    }

    static (
        CommandOption verbose,
        CommandOption silent,
        CommandOption rebuildReport,
        CommandOption noRefresh,
        CommandOption rebuildRepoList,
        CommandOption rebuildAll,
        CommandOption setupDefender,
        CommandOption scanOnly,
        CommandOption noMft,
        CommandOption noLocalCommitCount,
        CommandOption summary,
        CommandOption merge,
        CommandOption clearCache,
        CommandOption deleteAllLocalFiles,
        CommandOption printMinified,
        CommandOption dbSize,
        CommandOption allBranches,
        CommandOption watch,
        CommandOption filter,
        CommandOption paths,
        CommandOption savePath,
        CommandOption configPath
    ) SetupOptions(CommandLineApplication app)
    {
        // Boolean flags
        var verboseOption = app.Option("-v|--verbose", "Enable verbose logging.", CommandOptionType.NoValue);
        var silentOption = app.Option("-s|--silent", "Do not print to the console", CommandOptionType.NoValue);
        var rebuildReportOption = app.Option("--rebuild-report", "Rebuild the report from scratch (instead of using cache)", CommandOptionType.NoValue);
        var noRefreshOption = app.Option("--no-refresh", "Do not refresh the report based on the latest state; just print out the cached report", CommandOptionType.NoValue);
        var rebuildRepoListOption = app.Option("--rebuild-repo-list", "Rebuild the list of repositories (instead of using cache)", CommandOptionType.NoValue);
        var rebuildAllOption = app.Option("--rebuild-all", "Rebuild the report and list of repositories (instead of using cache)", CommandOptionType.NoValue);
        var setupDefenderOption = app.Option("--setup-defender", "Add Windows Defender exclusions for git/dotnet processes and search paths (triggers UAC prompt)", CommandOptionType.NoValue);
        var scanOnlyOption = app.Option("--scan-only", "Print discovered repository paths (one per line) and exit without refreshing", CommandOptionType.NoValue);
        var noMftOption = app.Option("--no-mft", "Skip MFT search and use recursive directory scan instead", CommandOptionType.NoValue);
        var noLocalCommitCountOption = app.Option("--no-local-commit-count", "Skip the expensive per-branch local-commit-count iteration in each repository refresh", CommandOptionType.NoValue);
        var summaryOption = app.Option("--summary", "Output a condensed summary (dirty/unpushed/stale counts + repos needing attention)", CommandOptionType.NoValue);
        var mergeOption = app.Option("--merge", "Targeted single-repo refresh: merge the repos named by --paths into the existing report at --save-path (insert/update those entries, leave all others intact), then write back atomically. Requires --paths and --save-path.", CommandOptionType.NoValue);
        var clearCacheOption = app.Option("--clear-cache", "Delete cached reports and configurations before running (combine with --no-refresh to avoid re-generating the cache)", CommandOptionType.NoValue);
        var deleteAllLocalFilesOption = app.Option("--delete-all-local-files", "Delete all files created by GitWizard before running (includes files deleted by --clear-cache; combine with --no-refresh to avoid creating any more local files)", CommandOptionType.NoValue);
        var printMinifiedOption = app.Option("--print-minified", "Print the output to the console as minified JSON", CommandOptionType.NoValue);
        var dbSizeOption = app.Option("--db-size", "Show the size of the GitWizard local files folder (~/.GitWizard/) and exit", CommandOptionType.NoValue);
        var allBranchesOption = app.Option("--all-branches", "Include all local branches (default + already-merged) in each repo's Branches list, not just actionable ones", CommandOptionType.NoValue);
        var watchOption = app.Option("--watch", "Watch tracked repositories for changes via MFTLib's USN journal broker (Windows only; triggers one UAC prompt) and print a line per repo that changes. Runs until Ctrl-C.", CommandOptionType.NoValue);

        // String options - use MultipleValue to allow missing args and duplicates (last value wins).
        var filterOption = app.Option("-f|--filter <PATTERN>", "Filter output to repositories whose path contains <PATTERN> (case-insensitive)", CommandOptionType.MultipleValue);
        var pathsOption = app.Option("-p|--paths <PATHS>", "Report on specific repo paths (newline-separated file or comma-separated list)", CommandOptionType.MultipleValue);
        var savePathOption = app.Option("--save-path <PATH>", "Path where the report will be saved to disk (otherwise it is only printed to the console)", CommandOptionType.MultipleValue);
        var configPathOption = app.Option("--config-path <PATH>", "Path to custom configuration file (otherwise global or default configuration is used)", CommandOptionType.MultipleValue);

        return (
            verboseOption,
            silentOption,
            rebuildReportOption,
            noRefreshOption,
            rebuildRepoListOption,
            rebuildAllOption,
            setupDefenderOption,
            scanOnlyOption,
            noMftOption,
            noLocalCommitCountOption,
            summaryOption,
            mergeOption,
            clearCacheOption,
            deleteAllLocalFilesOption,
            printMinifiedOption,
            dbSizeOption,
            allBranchesOption,
            watchOption,
            filterOption,
            pathsOption,
            savePathOption,
            configPathOption);
    }

    static RunConfigurationResult ExecuteHandler(
        CommandOption verboseOption,
        CommandOption silentOption,
        CommandOption rebuildReportOption,
        CommandOption noRefreshOption,
        CommandOption rebuildRepoListOption,
        CommandOption rebuildAllOption,
        CommandOption setupDefenderOption,
        CommandOption scanOnlyOption,
        CommandOption noMftOption,
        CommandOption noLocalCommitCountOption,
        CommandOption summaryOption,
        CommandOption mergeOption,
        CommandOption clearCacheOption,
        CommandOption deleteAllLocalFilesOption,
        CommandOption printMinifiedOption,
        CommandOption dbSizeOption,
        CommandOption allBranchesOption,
        CommandOption watchOption,
        CommandOption filterOption,
        CommandOption pathsOption,
        CommandOption savePathOption,
        CommandOption configPathOption)
    {
        var hasError = false;

        // Apply side-effects first (logging mode).
        GitWizardLog.VerboseMode = verboseOption.HasValue();
        GitWizardLog.SilentMode = silentOption.HasValue();

        // Get the last value for string options (last wins, like the old parser).
        string? GetLastValue(CommandOption opt)
        {
            if (opt.Values.Count == 0)
                return null;
            return opt.Values[opt.Values.Count - 1];
        }

        var savePath = GetLastValue(savePathOption);
        var configPath = GetLastValue(configPathOption);
        var filterValue = GetLastValue(filterOption);
        var pathsValue = GetLastValue(pathsOption);

        ValidateSavePath(ref savePath, ref hasError);
        ValidateConfigPath(ref configPath, ref hasError);

        // --scan-only implies --rebuild-repo-list.
        var scanOnly = scanOnlyOption.HasValue();
        var rebuildRepoList = scanOnly || rebuildRepoListOption.HasValue() || rebuildAllOption.HasValue();

        // --rebuild-all implies --rebuild-report.
        var rebuildAll = rebuildAllOption.HasValue();
        var rebuildReport = rebuildAll || rebuildReportOption.HasValue();

        return new RunConfigurationResult(
            rebuildRepoList,
            rebuildReport,
            clearCacheOption.HasValue(),
            deleteAllLocalFilesOption.HasValue(),
            setupDefenderOption.HasValue(),
            scanOnly,
            noMftOption.HasValue(),
            filterValue,
            pathsValue,
            summaryOption.HasValue(),
            mergeOption.HasValue(),
            !noRefreshOption.HasValue(),
            printMinifiedOption.HasValue(),
            savePath,
            configPath,
            dbSizeOption.HasValue(),
            allBranchesOption.HasValue(),
            watchOption.HasValue(),
            noLocalCommitCountOption.HasValue(),
            hasError);
    }

    static void ValidateSavePath(ref string? savePath, ref bool hasError)
    {
        if (savePath != null && !string.IsNullOrEmpty(savePath))
        {
            var parentDir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                GitWizardLog.Log($"The directory for --save-path does not exist: {parentDir}", GitWizardLog.LogType.Error);
                hasError = true;
                savePath = null;
            }
        }
    }

    static void ValidateConfigPath(ref string? configPath, ref bool hasError)
    {
        if (configPath != null && !string.IsNullOrEmpty(configPath))
        {
            if (!File.Exists(configPath))
            {
                GitWizardLog.Log($"The configuration file does not exist: {configPath}", GitWizardLog.LogType.Error);
                hasError = true;
                configPath = null;
            }
        }
    }

    static RunConfigurationResult CreateDefaultResult() =>
        new(
            false, false, false, false, false, false, false, null, null,
            false, false, true, false, null, null, false, false, false, false, false);
}

sealed record RunConfigurationResult(
    bool RebuildRepositoryList,
    bool RebuildReport,
    bool ClearCache,
    bool DeleteAllLocalFiles,
    bool SetupDefender,
    bool ScanOnly,
    bool NoMft,
    string? FilterPattern,
    string? PathsArgument,
    bool Summary,
    bool Merge,
    bool RefreshReport,
    bool Minified,
    string? SavePath,
    string? CustomConfigurationPath,
    bool DbSize,
    bool AllBranches,
    bool Watch,
    bool NoLocalCommitCount,
    bool HasError)
{
    /// <summary>
    /// True when McMaster already printed help or version text and the caller should exit
    /// immediately without running any report generation (e.g., --help, --version).
    /// </summary>
    public bool ExitRequested { get; init; }
}
