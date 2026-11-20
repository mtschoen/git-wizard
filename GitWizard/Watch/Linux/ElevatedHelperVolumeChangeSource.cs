using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace GitWizard.Watch.Linux;

/// <summary>
/// Linux <see cref="IVolumeChangeSource"/> for a desktop process that is not root. Arming starts
/// this executable's <see cref="LinuxWatchHelperHost.Flag"/> mode through pkexec (one polkit
/// prompt), which runs <see cref="FanotifyVolumeChangeSource"/> as root; this source replays the
/// helper's arm result, readiness, pump start, batches and failures from its stdout. The helper is
/// started once per source. Until the helper's <c>hello</c> arrives, the launched process is still
/// pkexec running as the caller, showing its dialog: cancelling the arm or disposing terminates it,
/// which closes the dialog. After <c>hello</c> it is root and is never signalled; it exits when
/// this source closes its stdin.
/// </summary>
/// <remarks>
/// Every helper end this source did not ask for (a <c>died</c> frame, stdout ending, the process
/// exiting) excludes the watched roots and raises <see cref="SourceDied"/> once; a subscriber that
/// attaches after the end receives the recorded reason when it subscribes, so a helper that died
/// between arming and watching is not lost.
/// </remarks>
public sealed partial class ElevatedHelperVolumeChangeSource :
    IVolumeChangeSource, IVolumeReadiness, IVolumeChangeDiagnostics, IPumpLifecycle, IElevationConnection
{
    internal const string AuthenticationCancelledMessage = "Authentication was cancelled, so Live stays off.";
    internal const string AuthorizationFailedMessage =
        "Live stays off. The dialog may have been cancelled, authorization may have failed, or no polkit authentication agent may be available.";
    internal const string PkexecMissingMessage =
        "polkit's pkexec is not installed, so Live cannot request elevation. Starting GitWizardUI as root needs no prompt.";
    internal const string LaunchFailedMessage = "The elevated Live helper could not be started.";
    internal const string UnknownExecutableMessage = "The GitWizardUI executable path is unknown, so the elevated Live helper cannot be started.";
    internal const string DotnetHostMessage =
        "Live elevation needs the GitWizardUI executable itself; it cannot be started through the dotnet host.";
    internal const string StoppedMessage = "The Live watch was stopped.";
    internal const string RelativeRootMessage = "Live watch roots must be absolute paths.";
    internal const string RejectedRequestMessage = "The elevated Live helper rejected the watch request.";
    const int PkexecDismissedExitCode = 126;
    const int PkexecNotAuthorizedExitCode = 127;
    const int FileNotFoundErrorNumber = 2;

    static readonly UTF8Encoding Utf8WithoutMarker = new(false);
    static readonly TimeSpan DefaultExitWait = TimeSpan.FromSeconds(5);

    readonly ProcessStartInfo? _startInfo;
    readonly LinuxWatchHelperLauncher _launcher;
    readonly TimeProvider _timeProvider;
    readonly TimeSpan _exitWait;
    readonly Func<string, bool> _isConclusivelyMissing;
    readonly Lock _gate = new();
    readonly Lock _launchGate = new();
    readonly SemaphoreSlim _inputGate = new(1, 1);
    readonly Channel<VolumeChangeBatch> _batches = Channel.CreateUnbounded<VolumeChangeBatch>();
    readonly TaskCompletionSource _pumpStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<IReadOnlyDictionary<string, string>> _armResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource _disposal = new();
    readonly string? _unavailableReason;
    readonly List<VolumeWatchStatus> _localExclusions = [];

    LinuxWatchHelperProcess? _process;
    Task? _readTask;
    VolumeReadinessSnapshot _readiness = VolumeReadinessSnapshot.Empty;
    IReadOnlyList<string> _roots = [];
    Action<string>? _sourceDied;
    string? _deathReason;
    bool _armStarted;
    int _watchRequested;
    bool _inputClosed;
    volatile bool _helloReceived;
    volatile bool _terminated;
    volatile bool _disposing;

    internal ElevatedHelperVolumeChangeSource(ProcessStartInfo? startInfo, LinuxWatchHelperLauncher launcher,
        TimeProvider timeProvider, TimeSpan exitWait, Func<string, bool>? isConclusivelyMissing = null,
        string? unavailableReason = null)
    {
        _unavailableReason = unavailableReason;
        _startInfo = startInfo;
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _exitWait = exitWait;
        _isConclusivelyMissing = isConclusivelyMissing ?? IsConclusivelyMissing;
    }

    /// <summary>The helper's launch command, or null when no helper can start.</summary>
    internal ProcessStartInfo? StartInfo => _startInfo;

    /// <summary>
    /// A source whose helper is <paramref name="executablePath"/>, started through pkexec. The host
    /// must dispatch <see cref="LinuxWatchHelperHost.Flag"/>; the library never assumes the current
    /// process is that host, so the caller names the executable explicitly.
    /// </summary>
    public static ElevatedHelperVolumeChangeSource CreatePkexec(string? executablePath)
    {
        var unavailableReason = DescribeUnusableExecutable(executablePath);
        return new ElevatedHelperVolumeChangeSource(
            unavailableReason is null ? LinuxWatchHelperLaunch.CreatePkexecStartInfo(executablePath) : null,
            LinuxWatchHelperLaunch.Start, TimeProvider.System, DefaultExitWait, null, unavailableReason);
    }

    static string? DescribeUnusableExecutable(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return UnknownExecutableMessage;
        }

        return LinuxWatchHelperLaunch.IsDotnetHost(executablePath) ? DotnetHostMessage : null;
    }

    public event Action? Connecting;
    public event Action? Connected;
    public event Action<VolumeReadinessSnapshot>? ReadinessChanged;
    public event Action<string, string>? DriveFailed;

    // fanotify reports neither journal nor capacity warnings; the helper forwards none.
    public event Action<JournalWarning>? JournalWarningDetected { add { } remove { } }
    public event Action? CapacityWarningsChanged { add { } remove { } }

    public event Action<string>? SourceDied
    {
        add
        {
            string? recorded;
            lock (_gate)
            {
                _sourceDied += value;
                recorded = _deathReason;
            }

            if (recorded is not null)
            {
                value?.Invoke(recorded);
            }
        }
        remove
        {
            lock (_gate)
            {
                _sourceDied -= value;
            }
        }
    }

    public WatchReadinessMode Mode => WatchReadinessMode.FilesystemMarks;

    public VolumeReadinessSnapshot Readiness
    {
        get
        {
            lock (_gate)
            {
                return _readiness;
            }
        }
    }

    public Task PumpStarted => _pumpStarted.Task;

    public async Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ct.ThrowIfCancellationRequested();
        lock (_launchGate)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            if (_armStarted)
            {
                throw new InvalidOperationException("An elevated helper source is armed once.");
            }

            _armStarted = true;
        }

        var errors = new Dictionary<string, string>(WatchPath.StringComparer);
        var roots = new List<string>();
        foreach (var volume in volumes)
        {
            if (!IsAbsoluteRoot(volume))
            {
                Exclude(errors, volume, RelativeRootMessage);
            }
            else if (_isConclusivelyMissing(volume))
            {
                Exclude(errors, volume, $"Directory does not exist: {volume}");
            }
            else
            {
                roots.Add(volume);
            }
        }

        // Nothing watchable raises no prompt.
        if (roots.Count == 0)
        {
            return new VolumeArmResult(errors, []);
        }

        _roots = roots;
        ct.ThrowIfCancellationRequested();
        Connecting?.Invoke();
        using var onCancel = ct.Register(TerminateBeforeHello);
        if (LaunchUnlessStopped(ct) is { } launchFailure)
        {
            foreach (var root in roots)
            {
                Exclude(errors, root, launchFailure);
            }

            return new VolumeArmResult(errors, []);
        }

        // pkexec reads no stdin before it authenticates, so the arm waits for the helper's hello;
        // a large arm line could otherwise fill the pipe and block behind the dialog.
        try
        {
            if (await Task.WhenAny(_hello.Task, _ended.Task).WaitAsync(ct).ConfigureAwait(false) == _hello.Task)
            {
                await SendAsync(new ArmRequestFrame(roots), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!_helloReceived)
        {
            TerminateBeforeHello();
            throw;
        }

        var completed = await Task.WhenAny(_armResult.Task, _ended.Task).WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (completed == _armResult.Task)
        {
            foreach (var (root, message) in await _armResult.Task.ConfigureAwait(false))
            {
                errors[root] = message;
            }
        }
        else
        {
            var reason = await _ended.Task.ConfigureAwait(false);
            foreach (var root in roots)
            {
                errors[root] = reason;
            }
        }

        return new VolumeArmResult(errors, []);
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (_process is null)
        {
            throw new InvalidOperationException("ArmAndCatchUpAsync must start the helper before WatchAsync.");
        }

        if (Interlocked.Exchange(ref _watchRequested, 1) == 0)
        {
            await SendAsync(new WatchRequestFrame(), ct).ConfigureAwait(false);
        }

        await foreach (var batch in _batches.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Terminates pkexec while its dialog is still open (before <c>hello</c>); otherwise closes the
    /// root helper's stdin and waits, bounded, for it to exit without signalling it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_launchGate)
        {
            if (_disposing)
            {
                return;
            }

            _disposing = true;
        }

        if (_process is { } process)
        {
            TerminateBeforeHello();
            await _disposal.CancelAsync().ConfigureAwait(false);
            await CloseInputAsync(process).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(process.Exited, _readTask ?? Task.CompletedTask)
                    .WaitAsync(_exitWait, _timeProvider).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                GitWizardLog.Log("The elevated Live helper has not exited yet; it exits once it reads the end of its input.",
                    GitWizardLog.LogType.Warning);
            }
        }

        _batches.Writer.TryComplete();
        _ended.TrySetResult(StoppedMessage);
    }

    static bool IsAbsoluteRoot(string? volume) =>
        !string.IsNullOrWhiteSpace(volume) && (Path.IsPathFullyQualified(volume) || volume.StartsWith('/')) &&
        !volume.Contains('\0', StringComparison.Ordinal);

    // Only absence this process can prove: a root it may not traverse is left for root to judge.
    static bool IsConclusivelyMissing(string root)
    {
        try
        {
            var attributes = File.GetAttributes(root);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    string? LaunchUnlessStopped(CancellationToken ct)
    {
        lock (_launchGate)
        {
            // Stop either wins admission or takes ownership of the published pkexec process.
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposing, this);
            return Launch();
        }
    }

    // Returns the user-facing failure, or null once the helper's pipes are being read.
    string? Launch()
    {
        if (_startInfo is null)
        {
            return _unavailableReason ?? UnknownExecutableMessage;
        }

        LinuxWatchHelperProcess process;
        try
        {
            process = _launcher(_startInfo);
        }
        catch (Win32Exception exception)
        {
            GitWizardLog.LogException(exception, $"Starting {_startInfo.FileName} for the elevated Live helper failed.");
            return exception.NativeErrorCode == FileNotFoundErrorNumber ? PkexecMissingMessage : LaunchFailedMessage;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            GitWizardLog.LogException(exception, $"Starting {_startInfo.FileName} for the elevated Live helper failed.");
            return LaunchFailedMessage;
        }

        _process = process;
        _readTask = Task.Run(() => ReadMessagesAsync(process));
        _ = Task.Run(() => DrainErrorAsync(process.Error));
        return null;
    }

    void Exclude(Dictionary<string, string> errors, string volume, string message)
    {
        errors[volume] = message;
        var status = VolumeWatchStatus.Excluded(volume, VolumeBlockOrigin.Unknown, message);
        lock (_gate)
        {
            _localExclusions.Add(status);
        }

        PublishReadiness(snapshot => snapshot.With(status));
    }

    // A helper readiness frame describes only the roots it was sent, so the roots this source
    // excluded itself stay in the snapshot beneath it.
    VolumeReadinessSnapshot MergeOverLocalExclusions(IReadOnlyList<VolumeWatchStatus> helperVolumes)
    {
        lock (_gate)
        {
            return _localExclusions.Concat(helperVolumes)
                .Aggregate(VolumeReadinessSnapshot.Empty, (snapshot, status) => snapshot.With(status));
        }
    }

    void PublishReadiness(Func<VolumeReadinessSnapshot, VolumeReadinessSnapshot> update)
    {
        lock (_gate)
        {
            var snapshot = _readiness = update(_readiness);
            try { ReadinessChanged?.Invoke(snapshot); }
            catch (Exception exception) { GitWizardLog.LogException(exception, "A Live readiness subscriber failed."); }
        }
    }
}
