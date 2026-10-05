using System.Diagnostics;
using GitWizard.Watch;

// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

public static partial class Program
{
    internal static Func<ProcessStartInfo, Process?> _startProcess = Process.Start;
    internal static Func<string?> _getProcessPath = () => Environment.ProcessPath;
    internal static Func<bool> _isElevated = VolumeChangeSourceFactory.IsElevated;
    internal static Func<bool> _isWindows = OperatingSystem.IsWindows;
    internal static Func<bool> _isLinux = OperatingSystem.IsLinux;
    internal static Func<IVolumeChangeSource> _sourceFactory = VolumeChangeSourceFactory.Create;
    internal static Func<string> _getLocalFilesPath = GitWizardApi.GetLocalFilesPath;

    internal static void ResetWatchDependenciesToDefault()
    {
        _startProcess = Process.Start;
        _getProcessPath = () => Environment.ProcessPath;
        _isElevated = VolumeChangeSourceFactory.IsElevated;
        _isWindows = OperatingSystem.IsWindows;
        _isLinux = OperatingSystem.IsLinux;
        _sourceFactory = VolumeChangeSourceFactory.Create;
        _getLocalFilesPath = GitWizardApi.GetLocalFilesPath;
    }

    /// <summary>
    /// --watch: auto-detect changes in tracked repositories via platform volume change detection
    /// (Windows USN journal broker with single UAC prompt, or Linux fanotify with single pkexec prompt),
    /// driven through <see cref="RepositoryWatchService"/>.
    /// Prints one line per repository event as it arrives and runs until Ctrl-C or the broker dies.
    /// </summary>
    internal static async Task<int> RunWatchAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator? coordinator = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        if (_isLinux() && !_isElevated())
        {
            return await RunElevatedWatchViaPkexecAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (!_isWindows() && !_isLinux())
        {
            await stderr.WriteLineAsync("--watch requires Windows or Linux.").ConfigureAwait(false);
            return 1;
        }

        coordinator ??= new RepositoryDiscoveryCoordinator();

        // Retained exact roots keep cached repositories outside the configured search roots in
        // both discovery and the journal watch's selected drive set.
        var retainedRoots = await ReadRetainedRootsAsync(cancellationToken).ConfigureAwait(false);
        var request = new DiscoveryRequest(
            configuration,
            DiscoveryPurpose.Watch,
            false,
            runConfiguration.NoMft,
            retainedRoots);

        using var ctrlC = new CtrlCCancellation();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ctrlC.Token, cancellationToken);
        var token = linkedCts.Token;

        var verbose = GitWizardLog.VerboseMode;
        var writer = new WatchLineWriter(stdout.WriteLine, verbose, TimeProvider.System);
        var startupProgress = new SynchronousProgress<WatchStartupProgress>(writer.WriteStartup);
        await using var startup = await coordinator.PrepareWatchAsync(request, null, startupProgress, token).ConfigureAwait(false);
        WriteDiscoveryDiagnostics(startup.Inventory, stderr);

        if (startup.Inventory.Outcome == DiscoveryOutcome.Unusable)
            return 1;

        if (startup.Source is null)
        {
            if (startup.Inventory.CanPublishFullInventory)
            {
                await stdout.WriteLineAsync("No tracked repositories found; nothing to watch.").ConfigureAwait(false);
                return 0;
            }
            return 1;
        }

        var service = new RepositoryWatchService(startup);
        int? capturedExit = null;
        var stopped = await RunWatchLoopAsync(service, stdout.WriteLine, exitCode => capturedExit = exitCode, token, verbose, TimeProvider.System).ConfigureAwait(false);

        if (capturedExit is { } exitVal)
            return exitVal;

        return stopped ? 1 : 0;
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
        CancellationToken cancellationToken,
        bool verbose = false,
        TimeProvider? clock = null)
    {
        var writer = new WatchLineWriter(writeLine, verbose, clock ?? TimeProvider.System);
        var stopped = false;
        var reported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reportedWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reportingLock = new Lock();
        void OnStopped(string reason)
        {
            stopped = true;
            writer.Write($"Broker died: {reason}");
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
                    writer.Write($"scan error on {drive}: {message}");
                }
            }
        }
        void OnScanWarningsChanged()
        {
            lock (reportingLock)
            {
                foreach (var warning in service.ScanWarnings)
                {
                    // Keyed on the message rather than the drive: a live-watch loss rephrases
                    // the drive's arm-time hint, and the new wording must print again.
                    if (!reportedWarnings.Add(warning.Message))
                        continue;
                    writer.Write(warning.Message);
                }
            }
        }
        void OnReady(WatchReadiness readiness) => writer.Write(FormatReadyLine(readiness));
        void OnVolumeReadinessChanged(VolumeReadinessSnapshot snapshot) => writer.WriteVolumes(snapshot);

        service.Stopped += OnStopped;
        service.ScanErrorsChanged += OnScanErrorsChanged;
        service.ScanWarningsChanged += OnScanWarningsChanged;
        service.Ready += OnReady;
        service.VolumeReadinessChanged += OnVolumeReadinessChanged;
        try
        {
            return await RunWatchEventsAsync(service, writer, exit, () => stopped, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            service.Stopped -= OnStopped;
            service.ScanErrorsChanged -= OnScanErrorsChanged;
            service.ScanWarningsChanged -= OnScanWarningsChanged;
            service.Ready -= OnReady;
            service.VolumeReadinessChanged -= OnVolumeReadinessChanged;
        }
    }

    static async Task<bool> RunWatchEventsAsync(
        RepositoryWatchService service, WatchLineWriter writer, Action<int> exit,
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
            writer.Write($"Could not start the journal broker: {exception.Message}");
            exit(1);
            return true;
        }

        while (hasNext)
        {
            writer.Write(FormatChangeEvent(enumerator.Current));
            try
            {
                hasNext = await MoveNextIgnoringCancellationAsync(enumerator).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                if (!isStopped())
                    writer.Write($"Broker died: {exception.Message}");

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

    // Discover before arming so repositories created since the last cache write are tracked.
    internal static async Task<List<string>> LoadTrackedRepositoryPathsAsync(
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator? coordinator = null,
        CancellationToken cancellationToken = default)
    {
        coordinator ??= new RepositoryDiscoveryCoordinator();
        var retainedRoots = await ReadRetainedRootsAsync(cancellationToken).ConfigureAwait(false);
        var request = new DiscoveryRequest(
            configuration,
            DiscoveryPurpose.Watch,
            false,
            false,
            retainedRoots);

        await using var startup = await coordinator.PrepareWatchAsync(request, null, cancellationToken).ConfigureAwait(false);
        return startup.Inventory.Paths.ToList();
    }

    static async Task<IReadOnlyList<string>> ReadRetainedRootsAsync(CancellationToken cancellationToken) =>
        await GitWizardApi.GetCachedRepositoryPathsAsync(cancellationToken).ConfigureAwait(false) ?? [];
}
