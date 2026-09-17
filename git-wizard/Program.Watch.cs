using System.Diagnostics;
using GitWizard.Watch;
using MFTLib;

// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

public static partial class Program
{
    internal static Func<ProcessStartInfo, Process?> _startProcess = Process.Start;
    internal static Func<string?> _getProcessPath = () => Environment.ProcessPath;
    internal static Func<bool> _isElevated = VolumeChangeSourceFactory.IsElevated;
    internal static Func<IVolumeChangeSource> _sourceFactory = VolumeChangeSourceFactory.Create;
    internal static Func<string> _getLocalFilesPath = GitWizardApi.GetLocalFilesPath;

    internal static void ResetWatchDependenciesToDefault()
    {
        _startProcess = Process.Start;
        _getProcessPath = () => Environment.ProcessPath;
        _isElevated = VolumeChangeSourceFactory.IsElevated;
        _sourceFactory = VolumeChangeSourceFactory.Create;
        _getLocalFilesPath = GitWizardApi.GetLocalFilesPath;
    }

    /// <summary>
    /// Dispatch MFTLib's elevated journal-broker child-process mode (the <c>--broker</c>
    /// flag). Returns true when this process was the elevated broker child - production
    /// never returns from that path, the runner exits the process itself; a normal launch
    /// matches no flag and returns false so <see cref="Main"/> continues its usual startup.
    /// </summary>
    internal static bool TryHandleElevatedBrokerEntry(string[] args, IElevatedEntryRunner runner) =>
        ElevatedEntryPoint.TryHandle(args, runner);

    /// <summary>
    /// --watch: auto-detect changes in tracked repositories via platform volume change detection
    /// (Windows USN journal broker with single UAC prompt, or Linux fanotify with single pkexec prompt),
    /// driven through <see cref="RepositoryWatchService"/>.
    /// Prints one line per repository event as it arrives and runs until Ctrl-C or the broker dies.
    /// </summary>
    static async Task RunWatchAsync(GitWizardConfiguration configuration)
    {
        if (OperatingSystem.IsLinux() && !_isElevated())
        {
            var elevatedExitCode = await RunElevatedWatchViaPkexecAsync().ConfigureAwait(false);
            Environment.Exit(elevatedExitCode);
            return;
        }

        var repositoryPaths = await LoadTrackedRepositoryPathsAsync(configuration).ConfigureAwait(false);
        if (repositoryPaths.Count == 0)
        {
            Console.WriteLine("No tracked repositories found; nothing to watch.");
            return;
        }

        var searchRoots = configuration.SearchPaths
            .Select(GitWizardApi.ExpandSearchPath)
            .OfType<string>()
            .ToList();
        var ignoredRoots = GitWizardApi.ExpandIgnoredPaths(configuration.IgnoredPaths);

        using var ctrlC = new CtrlCCancellation();
        await using var source = _sourceFactory();
        var service = new RepositoryWatchService(source, repositoryPaths, searchRoots,
            includeTempDirectories: configuration.IncludeTempDirectories == true,
            ignoredPaths: ignoredRoots);

        var stopped = await RunWatchLoopAsync(service, Console.WriteLine, Environment.Exit, ctrlC.Token)
            .ConfigureAwait(false);
        if (stopped)
            Environment.Exit(1);
    }

    internal static async Task<int> RunElevatedWatchViaPkexecAsync(
        string[]? commandLineArgs = null,
        Action<string>? logError = null,
        CancellationToken cancellationToken = default)
    {
        logError ??= message => GitWizardLog.Log(message, GitWizardLog.LogType.Error);

        var processPath = _getProcessPath();
        if (string.IsNullOrEmpty(processPath))
        {
            logError("Cannot determine the current executable path to launch pkexec.");
            return 1;
        }

        var localFilesPath = _getLocalFilesPath();

        var startInfo = new ProcessStartInfo
        {
            FileName = "pkexec",
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("env");
        startInfo.ArgumentList.Add($"GITWIZARD_HOME={localFilesPath}");
        startInfo.ArgumentList.Add(processPath);
        var args = commandLineArgs ?? Environment.GetCommandLineArgs().Skip(1);
        foreach (var argument in args)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = _startProcess(startInfo);
            if (process == null)
            {
                logError("Failed to start pkexec process.");
                return 1;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Exception exception)
        {
            logError($"Could not start pkexec: {exception.Message}");
            return 1;
        }
    }

    // Drains service.RunAsync, printing one line per RepositoryChangeEvent kind plus one line
    // per new or changed drive failure, and reports whether the underlying source died mid-run.
    // Internal (rather than folded into RunWatchAsync) so GitWizardTests can drive it with a
    // fake-source-backed RepositoryWatchService, a capturing sink, and a fake exit, without
    // touching the real Environment.Exit or a real elevated broker. The exit callback (defaulted
    // to Environment.Exit by the production caller) fires the non-zero exit for a failed broker
    // spawn - the one terminal condition that must exit before any events flow.
    internal static async Task<bool> RunWatchLoopAsync(
        RepositoryWatchService service, Action<string> writeLine, Action<int> exit,
        CancellationToken cancellationToken)
    {
        var stopped = false;
        var reported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reportedWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reportingLock = new Lock();
        void OnStopped(string reason)
        {
            stopped = true;
            writeLine($"Broker died: {reason}");
        }
        void OnScanErrorsChanged()
        {
            lock (reportingLock)
            {
                foreach (var (drive, message) in service.ScanErrors)
                {
                    if (reported.TryGetValue(drive, out var previous) && previous == message)
                        continue;
                    reported[drive] = message;
                    writeLine($"scan error on {drive}: {message}");
                }
            }
        }
        void OnScanWarningsChanged()
        {
            lock (reportingLock)
            {
                foreach (var warning in service.ScanWarnings)
                {
                    if (reportedWarnings.Contains(warning.DriveLetter))
                        continue;
                    reportedWarnings.Add(warning.DriveLetter);
                    writeLine(warning.Message);
                }
            }
        }

        service.Stopped += OnStopped;
        service.ScanErrorsChanged += OnScanErrorsChanged;
        service.ScanWarningsChanged += OnScanWarningsChanged;
        try
        {
            return await RunWatchEventsAsync(service, writeLine, exit, () => stopped, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            service.Stopped -= OnStopped;
            service.ScanErrorsChanged -= OnScanErrorsChanged;
            service.ScanWarningsChanged -= OnScanWarningsChanged;
        }
    }

    static async Task<bool> RunWatchEventsAsync(
        RepositoryWatchService service, Action<string> writeLine, Action<int> exit,
        Func<bool> isStopped, CancellationToken cancellationToken)
    {
        await using var enumerator = service.RunAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        bool hasNext;
        try
        {
            hasNext = await MoveNextIgnoringCancellationAsync(enumerator).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            // A declined or failed broker connection surfaces from the initial index arming.
            // Report a clean startup failure rather than an unhandled stack trace.
            writeLine($"Could not start the journal broker: {exception.Message}");
            exit(1);
            return true;
        }

        while (hasNext)
        {
            writeLine(FormatChangeEvent(enumerator.Current));
            try
            {
                hasNext = await MoveNextIgnoringCancellationAsync(enumerator).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                if (!isStopped())
                    writeLine($"Broker died: {exception.Message}");

                return true;
            }
        }

        return isStopped();
    }

    // Ctrl-C and broker death both cancel the token backing RunAsync's enumeration; both are
    // expected ways for the loop to end, not failures to propagate.
    static async Task<bool> MoveNextIgnoringCancellationAsync(IAsyncEnumerator<RepositoryChangeEvent> enumerator)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    static string FormatChangeEvent(RepositoryChangeEvent changeEvent) =>
        changeEvent.Kind switch
        {
            RepositoryChangeKind.Changed => $"changed: {changeEvent.RepoRoot}",
            RepositoryChangeKind.Created => $"created: {changeEvent.RepoRoot}",
            RepositoryChangeKind.Deleted => $"deleted: {changeEvent.RepoRoot}",
            _ => $"{changeEvent.Kind.ToString().ToLowerInvariant()}: {changeEvent.RepoRoot}",
        };

    /// <summary>
    /// Bridges Ctrl-C to a <see cref="CancellationTokenSource"/> for the --watch command's
    /// lifetime. A small owned type - the source is an instance field the handler reaches
    /// via <c>this</c>, not a captured local - rather than a bare
    /// <see cref="CancellationTokenSource"/> plus a lambda subscribed to
    /// <see cref="Console.CancelKeyPress"/>: that shape would let the static,
    /// process-lifetime event keep the token source reachable (and callable) past this
    /// type's own <see cref="Dispose"/>.
    /// </summary>
    sealed class CtrlCCancellation : IDisposable
    {
        readonly CancellationTokenSource _source = new();

        public CtrlCCancellation() => Console.CancelKeyPress += OnCancelKeyPress;

        public CancellationToken Token => _source.Token;

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            _ = _source.CancelAsync();
        }

        public void Dispose()
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            _source.Dispose();
        }
    }

    // Loads the tracked repository list from the cache, falling back to a fresh discovery
    // scan (and re-saving the cache) when no cached list exists yet.
    static async Task<List<string>> LoadTrackedRepositoryPathsAsync(GitWizardConfiguration configuration)
    {
        var cached = await GitWizardApi.GetCachedRepositoryPathsAsync().ConfigureAwait(false);
        if (cached is { Length: > 0 })
            return cached.ToList();

        var discovered = new SortedSet<string>();
        configuration.GetRepositoryPaths(discovered);
        EnsureUsableSearchPaths(configuration);
        var paths = discovered.ToList();
        if (paths.Count > 0)
            await GitWizardApi.SaveCachedRepositoryPathsAsync(paths).ConfigureAwait(false);

        return paths;
    }
}
