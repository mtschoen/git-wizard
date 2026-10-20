// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

using McMaster.Extensions.CommandLineUtils;

static partial class CliParser
{
    /// <summary>
    /// A registered subcommand: its application (to recognize it as the selected command), its
    /// help option, and the builder that turns the parsed options into a configuration.
    /// </summary>
    sealed record SubcommandHandle(
        CommandLineApplication Application,
        CommandOption HelpOption,
        Func<RunConfiguration> Build);

    /// <summary>
    /// The options every inventory subcommand shares, registered after the subcommand's own
    /// options so help output lists them last.
    /// </summary>
    sealed record InventoryOptions(
        CommandOption Paths,
        CommandOption Filter,
        CommandOption SavePath,
        CommandOption ConfigPath,
        CommandOption Minified,
        CommandOption PrintMinified,
        CommandOption NoMft,
        CommandOption Verbose,
        CommandOption Silent,
        CommandOption Help);

    static SubcommandHandle SetupTagsCommand(CommandLineApplication app)
    {
        var tagsApp = app.Command("tags", _ => { });
        tagsApp.Description = "Scan all clones for tags matching a pattern and report whether they exist on remotes.";
        var matchOption = tagsApp.Option("-m|--match <PATTERN>", "Glob pattern to match tag names against (e.g. 'archive/*')", CommandOptionType.SingleValue);
        var inventoryOptions = AddInventoryOptions(tagsApp);

        return new SubcommandHandle(tagsApp, inventoryOptions.Help, () =>
            BuildInventoryConfiguration(inventoryOptions, CommandKind.Tags) with
            {
                TagMatchPattern = matchOption.Value() ?? "*"
            });
    }

    static SubcommandHandle SetupBranchesCommand(CommandLineApplication app)
    {
        var branchesApp = app.Command("branches", _ => { });
        branchesApp.Description = "Scan all clones for local branches and report upstream divergence and unique commit counts.";
        var notMergedOption = branchesApp.Option("--not-merged <BRANCH>", "Filter to branches not merged into <BRANCH> (defaults to the repo's default branch)", CommandOptionType.SingleValue);
        var notOption = branchesApp.Option("--not <BRANCH>", "Alias for --not-merged <BRANCH>", CommandOptionType.SingleValue);
        var inventoryOptions = AddInventoryOptions(branchesApp);

        return new SubcommandHandle(branchesApp, inventoryOptions.Help, () =>
            BuildInventoryConfiguration(inventoryOptions, CommandKind.Branches) with
            {
                NotMergedBranch = notMergedOption.Value() ?? notOption.Value(),
                NotMergedOnly = notMergedOption.HasValue() || notOption.HasValue()
            });
    }

    static InventoryOptions AddInventoryOptions(CommandLineApplication subcommand) => new(
        Paths: subcommand.Option("-p|--paths <PATHS>", "Report on specific repo paths (newline-separated file or comma-separated list)", CommandOptionType.MultipleValue),
        Filter: subcommand.Option("-f|--filter <PATTERN>", "Filter output to repositories whose path contains <PATTERN> (case-insensitive)", CommandOptionType.MultipleValue),
        SavePath: subcommand.Option("--save-path <PATH>", "Path where the report will be saved to disk", CommandOptionType.MultipleValue),
        ConfigPath: subcommand.Option("--config-path <PATH>", "Path to custom configuration file", CommandOptionType.MultipleValue),
        Minified: subcommand.Option("--minified", "Print the output as minified JSON", CommandOptionType.NoValue),
        PrintMinified: subcommand.Option("--print-minified", "Alias for --minified", CommandOptionType.NoValue),
        NoMft: subcommand.Option("--no-mft", "Use explicit recursive directory discovery instead of MFT index search (for users unable to accept the UAC elevation prompt); does not promise an unelevated NTFS journal watch", CommandOptionType.NoValue),
        Verbose: subcommand.Option("-v|--verbose", "Enable verbose logging.", CommandOptionType.NoValue),
        Silent: subcommand.Option("-s|--silent", "Do not print to the console", CommandOptionType.NoValue),
        Help: subcommand.HelpOption("-h|-?|--help"));

    static RunConfiguration BuildInventoryConfiguration(InventoryOptions options, CommandKind command)
    {
        var hasError = false;
        GitWizardLog.VerboseMode = options.Verbose.HasValue();
        GitWizardLog.SilentMode = options.Silent.HasValue();

        var savePath = GetLastValue(options.SavePath);
        var configPath = GetLastValue(options.ConfigPath);
        ValidateSavePath(ref savePath, ref hasError);
        ValidateConfigPath(ref configPath, ref hasError);

        return new RunConfiguration
        {
            Command = command,
            NoMft = options.NoMft.HasValue(),
            FilterPattern = GetLastValue(options.Filter),
            PathsArgument = GetLastValue(options.Paths),
            RefreshReport = false,
            Minified = options.Minified.HasValue() || options.PrintMinified.HasValue(),
            SavePath = savePath,
            CustomConfigurationPath = configPath,
            HasParseError = hasError,
            Silent = options.Silent.HasValue()
        };
    }
}
