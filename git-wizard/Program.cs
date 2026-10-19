using MFTLib;

namespace GitWizard.CLI;

public static partial class Program
{
    const string SessionStartMessage = @"Session Start Message
=======================================================================================================================
git-wizard Session Started
=======================================================================================================================";

    public static async Task Main()
    {
        var args = Environment.GetCommandLineArgs();

        // Dispatch MFTLib's elevated journal-broker child mode (launched by --watch's
        // self-elevation) before any other startup work.
        if (ElevatedEntryPoint.RunBrokerIfRequested(args))
            return;

        if (TryHandleElevatedHelperModes(args))
            return;

        var runConfiguration = CliParser.ParseProcessArgs(args);

        // --help/--version already printed their text via McMaster; stop here rather than
        // falling through to a full report run (they carry no other flags to distinguish
        // them from a bare invocation).
        if (runConfiguration.Command == CommandKind.HelpPrinted)
            return;

        if (runConfiguration.HasParseError)
        {
            GitWizardLog.Log("Aborting due to CLI parsing errors.", GitWizardLog.LogType.Error);
            Environment.Exit(2);
            return;
        }

        switch (runConfiguration.Command)
        {
            case CommandKind.IndexCacheStatus:
                await RunIndexCacheStatusAsync();
                return;
            case CommandKind.ClearIndexCache:
                await RunIndexCacheClearAsync();
                return;
            case CommandKind.DbSize:
                Console.WriteLine(ByteSizeFormatter.Format(GitWizardApi.GetLocalFilesSize()));
                return;
        }

        if (TryHandleLocalFileFlags(runConfiguration))
            return;

        var exitCode = await RunCommandCoreAsync(runConfiguration).ConfigureAwait(false);
        if (exitCode != 0)
            Environment.Exit(exitCode);
    }

    internal static async Task<int> RunCommandCoreAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration? configuration = null,
        RepositoryDiscoveryCoordinator? coordinator = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        switch (runConfiguration.Command)
        {
            case CommandKind.Sweep:
                configuration ??= await GetConfigurationAsync(runConfiguration).ConfigureAwait(false);
                return await RunSweepAsync(runConfiguration, configuration, coordinator, stdout, stderr, cancellationToken).ConfigureAwait(false);
            case CommandKind.Tags:
                configuration ??= await GetConfigurationAsync(runConfiguration).ConfigureAwait(false);
                return await RunTagsAsync(runConfiguration, configuration, coordinator, stdout, stderr, cancellationToken).ConfigureAwait(false);
            case CommandKind.Branches:
                configuration ??= await GetConfigurationAsync(runConfiguration).ConfigureAwait(false);
                return await RunBranchesAsync(runConfiguration, configuration, coordinator, stdout, stderr, cancellationToken).ConfigureAwait(false);
            case CommandKind.Merge:
                configuration = await PrepareInteractiveRunAsync(runConfiguration, configuration).ConfigureAwait(false);
                RunMerge(runConfiguration, configuration);
                return 0;
            case CommandKind.ScanOnly:
                configuration = await PrepareInteractiveRunAsync(runConfiguration, configuration).ConfigureAwait(false);
                return await RunScanOnlyAsync(runConfiguration, configuration, coordinator, stdout, stderr, cancellationToken).ConfigureAwait(false);
            case CommandKind.Watch:
                configuration = await PrepareInteractiveRunAsync(runConfiguration, configuration).ConfigureAwait(false);
                return await RunWatchAsync(runConfiguration, configuration, coordinator, stdout, stderr, cancellationToken).ConfigureAwait(false);
            case CommandKind.Report:
                configuration = await PrepareInteractiveRunAsync(runConfiguration, configuration).ConfigureAwait(false);
                return await RunDefaultReportCoreAsync(runConfiguration, configuration, coordinator, stdout, stderr, cancellationToken).ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(runConfiguration), runConfiguration.Command, "This command exits before RunCommandCoreAsync.");
        }
    }

    // The session banner, configuration load and Defender setup shared by the report-style commands.
    static async Task<GitWizardConfiguration> PrepareInteractiveRunAsync(
        RunConfiguration runConfiguration, GitWizardConfiguration? configuration)
    {
        if (runConfiguration.Command == CommandKind.ScanOnly)
            GitWizardLog.LogMethod = Console.Error.WriteLine;

        GitWizardLog.Log(SessionStartMessage);
        configuration ??= await GetConfigurationAsync(runConfiguration).ConfigureAwait(false);

        if (runConfiguration.SetupDefender)
        {
            GitWizardLog.Log("Setting up Windows Defender exclusions...");
            WindowsDefender.AddExclusions();
        }

        return configuration;
    }

    // Handle elevated helper modes (launched by self-elevation). Returns true if one of the
    // helper-mode args was found and handled, meaning the caller should return immediately.
    internal static bool TryHandleElevatedHelperModes(string[] args)
    {
        if (!args.Contains("--elevated-defender"))
            return false;

        Environment.Exit(WindowsDefender.AddExclusions() ? 0 : 1);
        return true;
    }

    static async Task RunIndexCacheClearAsync()
    {
        var result = MftIndexCacheMaintenance.Clear(GitWizardApi.GetLocalFilesPath());
        Console.WriteLine(result.ToClearText());
        if (!result.Succeeded)
        {
            await Console.Error.WriteLineAsync("Index cache clear incomplete; see per-drive outcomes and errors above.");
        }

        Environment.Exit(result.Succeeded ? 0 : 1);
    }

    // Handle the flags that delete local files before any report is generated
    // (--delete-all-local-files, --clear-cache). Returns true if Main should return
    // immediately; false if execution should continue into the command.
    static bool TryHandleLocalFileFlags(RunConfiguration runConfiguration)
    {
        if (runConfiguration.DeleteAllLocalFiles)
        {
            // A refusal is a command failure, so it is printed even under --silent.
            if (!GitWizardApi.TryDeleteAllLocalFiles(out var refusal))
            {
                Console.Error.WriteLine(refusal);
                Environment.Exit(1);
                return true;
            }

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

    internal static string[]? ParseExplicitPaths(RunConfiguration runConfiguration)
    {
        var argument = runConfiguration.PathsArgument;
        if (string.IsNullOrEmpty(argument))
            return null;

        // If it's a file, read lines from it
        if (File.Exists(argument))
        {
            return File.ReadLines(argument)
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

    internal static bool EnsureUsableSearchPaths(GitWizardConfiguration configuration, Action<int>? exit = null)
    {
        var usableCount = configuration.SearchPaths.Count(path => GitWizardApi.ExpandSearchPath(path) != null);
        if (usableCount == 0)
        {
            GitWizardLog.SilentMode = false;
            foreach (var searchPath in configuration.SearchPaths)
                GitWizardLog.Log($"Could not get repository paths at {searchPath} because it is not a directory", GitWizardLog.LogType.Error);
            GitWizardLog.Log(
                $"{usableCount} of {configuration.SearchPaths.Count} configured search paths are usable",
                GitWizardLog.LogType.Error);
            (exit ?? Environment.Exit)(1);
            return false;
        }

        return true;
    }
}
