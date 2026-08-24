// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

using McMaster.Extensions.CommandLineUtils;

static partial class CliParser
{
    static CommandOption? SetupTagsCommand(CommandLineApplication app, Action<RunConfigurationResult> onExecute)
    {
        CommandOption? tagsHelpOption = null;
        app.Command("tags", tagsApp =>
        {
            tagsApp.Description = "Scan all clones for tags matching a pattern and report whether they exist on remotes.";
            var matchOption = tagsApp.Option("-m|--match <PATTERN>", "Glob pattern to match tag names against (e.g. 'archive/*')", CommandOptionType.SingleValue);
            var pathsOption = tagsApp.Option("-p|--paths <PATHS>", "Report on specific repo paths (newline-separated file or comma-separated list)", CommandOptionType.MultipleValue);
            var filterOption = tagsApp.Option("-f|--filter <PATTERN>", "Filter output to repositories whose path contains <PATTERN> (case-insensitive)", CommandOptionType.MultipleValue);
            var savePathOption = tagsApp.Option("--save-path <PATH>", "Path where the report will be saved to disk", CommandOptionType.MultipleValue);
            var configPathOption = tagsApp.Option("--config-path <PATH>", "Path to custom configuration file", CommandOptionType.MultipleValue);
            var printMinifiedOption = tagsApp.Option("--print-minified", "Print the output as minified JSON", CommandOptionType.NoValue);
            var noMftOption = tagsApp.Option("--no-mft", "Skip MFT search and use recursive directory scan instead", CommandOptionType.NoValue);
            var verboseOption = tagsApp.Option("-v|--verbose", "Enable verbose logging.", CommandOptionType.NoValue);
            var silentOption = tagsApp.Option("-s|--silent", "Do not print to the console", CommandOptionType.NoValue);
            tagsHelpOption = tagsApp.HelpOption("-h|-?|--help");

            tagsApp.OnExecute(() =>
            {
                var hasError = false;
                GitWizardLog.VerboseMode = verboseOption.HasValue();
                GitWizardLog.SilentMode = silentOption.HasValue();

                var savePath = GetLastValue(savePathOption);
                var configPath = GetLastValue(configPathOption);
                var filterValue = GetLastValue(filterOption);
                var pathsValue = GetLastValue(pathsOption);
                var matchValue = matchOption.Value() ?? "*";

                ValidateSavePath(ref savePath, ref hasError);
                ValidateConfigPath(ref configPath, ref hasError);

                onExecute(new RunConfigurationResult(
                    RebuildRepositoryList: false,
                    RebuildReport: false,
                    ClearCache: false,
                    DeleteAllLocalFiles: false,
                    SetupDefender: false,
                    ScanOnly: false,
                    NoMft: noMftOption.HasValue(),
                    FilterPattern: filterValue,
                    PathsArgument: pathsValue,
                    Summary: false,
                    Sweep: false,
                    Merge: false,
                    RefreshReport: false,
                    Minified: printMinifiedOption.HasValue(),
                    SavePath: savePath,
                    CustomConfigurationPath: configPath,
                    DbSize: false,
                    AllBranches: false,
                    Watch: false,
                    NoLocalCommitCount: false,
                    HasError: hasError)
                {
                    Tags = true,
                    TagMatchPattern = matchValue
                });
            });
        });

        return tagsHelpOption;
    }

    static CommandOption? SetupBranchesCommand(CommandLineApplication app, Action<RunConfigurationResult> onExecute)
    {
        CommandOption? branchesHelpOption = null;
        app.Command("branches", branchesApp =>
        {
            branchesApp.Description = "Scan all clones for local branches and report upstream divergence and unique commit counts.";
            var notMergedOption = branchesApp.Option("--not-merged <BRANCH>", "Filter to branches not merged into <BRANCH> (defaults to the repo's default branch)", CommandOptionType.SingleValue);
            var notOption = branchesApp.Option("--not <BRANCH>", "Alias for --not-merged <BRANCH>", CommandOptionType.SingleValue);
            var pathsOption = branchesApp.Option("-p|--paths <PATHS>", "Report on specific repo paths (newline-separated file or comma-separated list)", CommandOptionType.MultipleValue);
            var filterOption = branchesApp.Option("-f|--filter <PATTERN>", "Filter output to repositories whose path contains <PATTERN> (case-insensitive)", CommandOptionType.MultipleValue);
            var savePathOption = branchesApp.Option("--save-path <PATH>", "Path where the report will be saved to disk", CommandOptionType.MultipleValue);
            var configPathOption = branchesApp.Option("--config-path <PATH>", "Path to custom configuration file", CommandOptionType.MultipleValue);
            var printMinifiedOption = branchesApp.Option("--print-minified", "Print the output as minified JSON", CommandOptionType.NoValue);
            var noMftOption = branchesApp.Option("--no-mft", "Skip MFT search and use recursive directory scan instead", CommandOptionType.NoValue);
            var verboseOption = branchesApp.Option("-v|--verbose", "Enable verbose logging.", CommandOptionType.NoValue);
            var silentOption = branchesApp.Option("-s|--silent", "Do not print to the console", CommandOptionType.NoValue);
            branchesHelpOption = branchesApp.HelpOption("-h|-?|--help");

            branchesApp.OnExecute(() =>
            {
                var hasError = false;
                GitWizardLog.VerboseMode = verboseOption.HasValue();
                GitWizardLog.SilentMode = silentOption.HasValue();

                var savePath = GetLastValue(savePathOption);
                var configPath = GetLastValue(configPathOption);
                var filterValue = GetLastValue(filterOption);
                var pathsValue = GetLastValue(pathsOption);

                var notMergedHasValue = notMergedOption.HasValue();
                var notHasValue = notOption.HasValue();
                var notMergedOnly = notMergedHasValue || notHasValue;

                var baseBranch = notMergedOption.Value() ?? notOption.Value();

                ValidateSavePath(ref savePath, ref hasError);
                ValidateConfigPath(ref configPath, ref hasError);

                onExecute(new RunConfigurationResult(
                    RebuildRepositoryList: false,
                    RebuildReport: false,
                    ClearCache: false,
                    DeleteAllLocalFiles: false,
                    SetupDefender: false,
                    ScanOnly: false,
                    NoMft: noMftOption.HasValue(),
                    FilterPattern: filterValue,
                    PathsArgument: pathsValue,
                    Summary: false,
                    Sweep: false,
                    Merge: false,
                    RefreshReport: false,
                    Minified: printMinifiedOption.HasValue(),
                    SavePath: savePath,
                    CustomConfigurationPath: configPath,
                    DbSize: false,
                    AllBranches: false,
                    Watch: false,
                    NoLocalCommitCount: false,
                    HasError: hasError)
                {
                    Branches = true,
                    NotMergedBranch = baseBranch,
                    NotMergedOnly = notMergedOnly
                });
            });
        });

        return branchesHelpOption;
    }
}
