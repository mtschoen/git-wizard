// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

using McMaster.Extensions.CommandLineUtils;

static partial class CliParser
{
    /// <summary>
    /// Parses the process's raw command-line args (as returned by
    /// <see cref="Environment.GetCommandLineArgs"/>, which include the program path as
    /// element 0). Strips that leading token before handing off to <see cref="Parse"/>,
    /// which expects a program-name-excluded argument array (the same contract as
    /// <c>Main(string[] args)</c>).
    /// </summary>
    internal static RunConfiguration ParseProcessArgs(string[] processArgs) =>
        Parse(processArgs.Length > 0 ? processArgs[1..] : processArgs);

    static string[] NormalizeConsumerAliases(string[] args)
    {
        var normalized = new string[args.Length];
        var expectsValue = false;
        var index = 0;
        var endOfOptions = false;
        foreach (var argument in args)
        {
            if (endOfOptions || expectsValue)
            {
                expectsValue = false;
                normalized[index++] = argument;
                continue;
            }
            if (argument == "--")
            {
                endOfOptions = true;
                normalized[index++] = argument;
                continue;
            }
            var resolved = argument switch
            {
                "-paths" => "--paths",
                "-save-path" => "--save-path",
                "-silent" => "--silent",
                _ => argument
            };
            normalized[index++] = resolved;
            expectsValue = resolved is "--paths" or "-p" or "--save-path"
                or "--config-path" or "--filter" or "-f" or "--match" or "-m"
                or "--not" or "--not-merged" or "--base";
        }
        return normalized;
    }

    public static RunConfiguration Parse(string[] args)
    {
        using var app = new CommandLineApplication();
        app.Name = "git-wizard";
        app.Description = "Multi-project solution for scanning and reporting on git repositories.";

        var helpOption = app.HelpOption("-h|-?|--help");
        var version = typeof(CliParser).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
        var versionOption = app.VersionOption("--version", version);

        var rootOptions = SetupOptions(app);
        SubcommandHandle[] subcommands = [SetupTagsCommand(app), SetupBranchesCommand(app), SetupPruneCommand(app)];
        RunConfiguration? parsed;

        try
        {
            var parseResult = app.Parse(NormalizeConsumerAliases(args));
            parsed = parseResult.SelectedCommand == app
                ? ExecuteHandler(rootOptions)
                : subcommands.FirstOrDefault(subcommand => subcommand.Application == parseResult.SelectedCommand)?.Build();
        }
        catch (CommandParsingException ex)
        {
            // Parsing failed (e.g., unknown tokens like testhost.dll paths in tests);
            // return default configuration rather than throwing.
            GitWizardLog.Log($"CLI parse failed, using defaults: {ex.Message}", GitWizardLog.LogType.Verbose);
            // Unknown/unrecognized flags are surfaced as a parse error so that
            // stale callers fail loudly (exit 2) instead of silently running with
            // defaults - important during the CLI flag migration.
            return new RunConfiguration { HasParseError = true };
        }

        // McMaster prints the help/version text itself and skips OnExecute, but that leaves
        // no signal in the returned result - without this, the caller can't tell "--help" or
        // "--version" apart from "no args at all" and falls through to a full run.
        if (helpOption.HasValue() || versionOption.HasValue() || subcommands.Any(subcommand => subcommand.HelpOption.HasValue()))
        {
            return new RunConfiguration { Command = CommandKind.HelpPrinted };
        }

        return parsed ?? new RunConfiguration();
    }

    sealed record RootOptions(
        CommandOption Verbose,
        CommandOption Silent,
        CommandOption RebuildReport,
        CommandOption NoRefresh,
        CommandOption RebuildRepoList,
        CommandOption RebuildAll,
        CommandOption SetupDefender,
        CommandOption ScanOnly,
        CommandOption NoMft,
        CommandOption NoLocalCommitCount,
        CommandOption Json,
        CommandOption Sweep,
        CommandOption Merge,
        CommandOption ClearCache,
        CommandOption ClearIndexCache,
        CommandOption IndexCacheStatus,
        CommandOption DeleteAllLocalFiles,
        CommandOption PrintMinified,
        CommandOption Minified,
        CommandOption DbSize,
        CommandOption AllBranches,
        CommandOption Watch,
        CommandOption Filter,
        CommandOption Paths,
        CommandOption SavePath,
        CommandOption ConfigPath);

    static RootOptions SetupOptions(CommandLineApplication app)
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
        var noMftOption = app.Option("--no-mft", "Use uncached directory-walk indexes (WalkOnly), without MFT scanning, elevation, or NTFS journal watch", CommandOptionType.NoValue);
        var noLocalCommitCountOption = app.Option("--no-local-commit-count", "Skip the expensive per-branch local-commit-count iteration in each repository refresh", CommandOptionType.NoValue);
        var jsonOption = app.Option("--json", "Print the ordinary report as strict JSON", CommandOptionType.NoValue);
        var sweepOption = app.Option("--sweep", "Output automation-focused JSON: tracked dirty files, branches with commits absent from all remote-tracking refs, and stash counts", CommandOptionType.NoValue);
        var mergeOption = app.Option("--merge", "Targeted single-repo refresh: merge the repos named by --paths into the existing report at --save-path (insert/update those entries, leave all others intact), then write back atomically. Requires --paths and --save-path.", CommandOptionType.NoValue);
        var clearCacheOption = app.Option("--clear-cache", "Delete repositories.txt and report.json only; preserve configuration and persistent index blocks (combine with --no-refresh to avoid rebuilding the report/list).", CommandOptionType.NoValue);
        var clearIndexCacheOption = app.Option("--clear-index-cache",
            "Clear unlocked MFT index blocks in supported GitWizard policy directories, show per-drive results and fresh cache state, then exit. Takes precedence over other maintenance/report/watch flags; leaves lock files.",
            CommandOptionType.NoValue);
        var indexCacheStatusOption = app.Option("--index-cache-status",
            "Inspect the current MFT cache policy and exit without discovery or a broker; Windows only. Cannot combine with cache deletion flags.",
            CommandOptionType.NoValue);
        var deleteAllLocalFilesOption = app.Option("--delete-all-local-files", "Delete local files only when no mft-index subtree exists; refuse otherwise. This command does not clear persistent index blocks (combine with --no-refresh to avoid recreating local files).", CommandOptionType.NoValue);
        var printMinifiedOption = app.Option("--print-minified", "Compatibility formatting flag for automation commands only", CommandOptionType.NoValue);
        var minifiedOption = app.Option("--minified", "Compact JSON output (requires --json for ordinary reports)", CommandOptionType.NoValue);
        var dbSizeOption = app.Option("--db-size", "Show the size of the GitWizard local files folder (~/.GitWizard/) and exit", CommandOptionType.NoValue);
        var allBranchesOption = app.Option("--all-branches", "Include all branches (even clean/merged/no-work branches) in the report instead of only actionable ones", CommandOptionType.NoValue);
        var watchOption = app.Option("--watch", "Watch search paths for filesystem changes and print one line per repository event (changed:/created:/deleted: <path>). Prints one 'ready:' line when the watch is caught up: on Windows when every requested NTFS drive has applied its USN journal backlog or been excluded, on Linux when fanotify marks are installed and startup discovery finished (no historical backlog exists). With -v, lines carry an ISO-8601 UTC timestamp and per-drive 'drive:'/'startup:' diagnostics.", CommandOptionType.NoValue);

        // String options - use MultipleValue to allow missing args and duplicates (last value wins).
        var filterOption = app.Option("-f|--filter <PATTERN>", "Filter output to repositories whose path contains <PATTERN> (case-insensitive)", CommandOptionType.MultipleValue);
        var pathsOption = app.Option("-p|--paths <PATHS>", "Report on specific repo paths (newline-separated file or comma-separated list)", CommandOptionType.MultipleValue);
        var savePathOption = app.Option("--save-path <PATH>", "Path where the report will be saved to disk (otherwise it is only printed to the console)", CommandOptionType.MultipleValue);
        var configPathOption = app.Option("--config-path <PATH>", "Path to custom configuration file (otherwise global or default configuration is used)", CommandOptionType.MultipleValue);

        return new RootOptions(
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
            jsonOption,
            sweepOption,
            mergeOption,
            clearCacheOption,
            clearIndexCacheOption,
            indexCacheStatusOption,
            deleteAllLocalFilesOption,
            printMinifiedOption,
            minifiedOption,
            dbSizeOption,
            allBranchesOption,
            watchOption,
            filterOption,
            pathsOption,
            savePathOption,
            configPathOption);
    }

    static RunConfiguration ExecuteHandler(RootOptions options)
    {
        var hasError = false;

        // Apply side-effects first (logging mode).
        GitWizardLog.VerboseMode = options.Verbose.HasValue();
        GitWizardLog.SilentMode = options.Silent.HasValue();
        if (options.ScanOnly.HasValue())
        {
            GitWizardLog.LogMethod = Console.Error.WriteLine;
        }

        var savePath = GetLastValue(options.SavePath);
        var configPath = GetLastValue(options.ConfigPath);
        var filterValue = GetLastValue(options.Filter);
        var pathsValue = GetLastValue(options.Paths);

        ValidateSavePath(ref savePath, ref hasError);
        ValidateConfigPath(ref configPath, ref hasError);

        // --scan-only implies --rebuild-repo-list.
        var scanOnly = options.ScanOnly.HasValue();
        var rebuildRepoList = scanOnly || options.RebuildRepoList.HasValue() || options.RebuildAll.HasValue();

        // --rebuild-all implies --rebuild-report.
        var rebuildAll = options.RebuildAll.HasValue();
        var rebuildReport = rebuildAll || options.RebuildReport.HasValue();

        if (options.IndexCacheStatus.HasValue() && (options.ClearIndexCache.HasValue() ||
            options.ClearCache.HasValue() || options.DeleteAllLocalFiles.HasValue()))
        {
            GitWizardLog.Log("--index-cache-status cannot be combined with cache deletion flags.", GitWizardLog.LogType.Error);
            hasError = true;
        }

        var command = ResolveCommand(options);
        var json = options.Json.HasValue();
        var minified = options.Minified.HasValue() || options.PrintMinified.HasValue();
        hasError |= HasInvalidReportFormat(options, command, minified, json);

        return new RunConfiguration
        {
            Command = command,
            RebuildRepositoryList = rebuildRepoList,
            RebuildReport = rebuildReport,
            ClearCache = options.ClearCache.HasValue(),
            DeleteAllLocalFiles = options.DeleteAllLocalFiles.HasValue(),
            SetupDefender = options.SetupDefender.HasValue(),
            NoMft = options.NoMft.HasValue(),
            FilterPattern = filterValue,
            PathsArgument = pathsValue,
            Json = json,
            RefreshReport = !options.NoRefresh.HasValue(),
            Minified = minified,
            SavePath = savePath,
            CustomConfigurationPath = configPath,
            AllBranches = options.AllBranches.HasValue(),
            NoLocalCommitCount = options.NoLocalCommitCount.HasValue(),
            HasParseError = hasError,
            Silent = options.Silent.HasValue()
        };
    }

    // The order is the order Program runs the commands in, so a command line that names several
    // modes keeps the one that used to win.
    static CommandKind ResolveCommand(RootOptions options)
    {
        if (options.IndexCacheStatus.HasValue())
        {
            return CommandKind.IndexCacheStatus;
        }

        if (options.ClearIndexCache.HasValue())
        {
            return CommandKind.ClearIndexCache;
        }

        if (options.DbSize.HasValue())
        {
            return CommandKind.DbSize;
        }

        if (options.Sweep.HasValue())
        {
            return CommandKind.Sweep;
        }

        if (options.Merge.HasValue())
        {
            return CommandKind.Merge;
        }

        if (options.ScanOnly.HasValue())
        {
            return CommandKind.ScanOnly;
        }

        return options.Watch.HasValue() ? CommandKind.Watch : CommandKind.Report;
    }

    static bool HasInvalidReportFormat(RootOptions options, CommandKind command, bool minified, bool json)
    {
        if (command != CommandKind.Report || (!options.PrintMinified.HasValue() && (!minified || json)))
        {
            return false;
        }

        GitWizardLog.Log("Ordinary reports use --json --minified for compact JSON.", GitWizardLog.LogType.Error);
        return true;
    }

    static string? GetLastValue(CommandOption opt)
    {
        return opt.Values.Count == 0 ? null : opt.Values[^1];
    }

    static void ValidateSavePath(ref string? savePath, ref bool hasError)
    {
        if (string.IsNullOrEmpty(savePath))
        {
            return;
        }

        var parentDir = Path.GetDirectoryName(savePath);
        if (string.IsNullOrEmpty(parentDir) || Directory.Exists(parentDir))
        {
            return;
        }

        GitWizardLog.Log($"The directory for --save-path does not exist: {parentDir}", GitWizardLog.LogType.Error);
        hasError = true;
        savePath = null;
    }

    static void ValidateConfigPath(ref string? configPath, ref bool hasError)
    {
        if (string.IsNullOrEmpty(configPath))
        {
            return;
        }

        if (File.Exists(configPath))
        {
            return;
        }

        GitWizardLog.Log($"The configuration file does not exist: {configPath}", GitWizardLog.LogType.Error);
        hasError = true;
        configPath = null;
    }
}
