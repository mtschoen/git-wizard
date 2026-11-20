using System.Text;
using System.Threading.Channels;

namespace GitWizard.Watch.Linux;

/// <summary>
/// One root helper session: requests arrive on stdin, and every frame the wrapped source produces
/// is queued and written to stdout by a single writer, so source callbacks never block on the pipe
/// and frames keep their order. The session owns and disposes the source.
/// </summary>
sealed class LinuxWatchHelperSession(Stream output, TextWriter diagnostics, IVolumeChangeSource source) : IDisposable
{
    static readonly UTF8Encoding Utf8WithoutMarker = new(false);

    readonly Lock _requestGate = new();
    readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource _stop = new();
    readonly TaskCompletionSource<int> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task? _armTask;
    Task? _watchTask;
    WatchPath[] _roots = [];
    WatchPath[] _rootParents = [];
    readonly Lock _readinessGate = new();
    VolumeReadinessSnapshot? _latestReadiness;
    bool _readinessSettled;
    volatile bool _armed;

    public async Task<int> RunAsync(Stream input, Action onStopping)
    {
        Subscribe();
        var writerTask = WriteOutboundAsync();
        Enqueue(new HelloFrame(LinuxWatchHelperProtocol.Version));

        // A blocking stdin read need not observe cancellation. Only join it when it reported EOF.
        var inputEnded = ReadRequestsAsync(input);

        var exitCode = await _finished.Task.ConfigureAwait(false);

        // Every stop trigger (stdin end, stdout failure, source death) passes here, so the host's
        // shutdown deadline covers all of the cleanup below whatever started the stop.
        onStopping();
        await _stop.CancelAsync().ConfigureAwait(false);
        _outbound.Writer.TryComplete();
        Task? arm;
        lock (_requestGate)
        {
            // A terminal callback can win while the reader is still assigning the task.
            arm = _armTask;
        }
        var cleanup = DisposeSourceWhenStoppedAsync(arm);
        if (exitCode == LinuxWatchHelperHost.ExitCodeSourceEnded)
        {
            // Half-closing stdin does not disconnect the stdout reader. Deliver the terminal
            // frame in order while the same shutdown deadline bounds both the drain and cleanup.
            await writerTask.ConfigureAwait(false);
        }
        else if (exitCode == LinuxWatchHelperHost.ExitCodeEndOfInput)
        {
            await inputEnded.ConfigureAwait(false);
        }

        await cleanup.ConfigureAwait(false);
        return exitCode;
    }

    async Task DisposeSourceWhenStoppedAsync(Task? arm)
    {
        await ObserveAsync(arm).ConfigureAwait(false);
        await ObserveAsync(_watchTask).ConfigureAwait(false);
        Unsubscribe();
        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await diagnostics.WriteLineAsync($"Disposing the watch source failed: {exception}").ConfigureAwait(false);
        }
    }

    public void Dispose() => _stop.Dispose();

    void Subscribe()
    {
        source.SourceDied += OnSourceDied;
        if (source is IVolumeReadiness readiness)
        {
            readiness.ReadinessChanged += OnReadinessChanged;
        }

        if (source is IVolumeChangeDiagnostics driveDiagnostics)
        {
            driveDiagnostics.DriveFailed += OnDriveFailed;
        }
    }

    void Unsubscribe()
    {
        source.SourceDied -= OnSourceDied;
        if (source is IVolumeReadiness readiness)
        {
            readiness.ReadinessChanged -= OnReadinessChanged;
        }

        if (source is IVolumeChangeDiagnostics driveDiagnostics)
        {
            driveDiagnostics.DriveFailed -= OnDriveFailed;
        }
    }

    void OnSourceDied(string reason) => Die(reason);
    // Arming publishes a status per root, and the desktop lists every tracked repository, so the
    // changes during the arm are coalesced: ArmAsync sends the settled list once, before armResult.
    void OnReadinessChanged(VolumeReadinessSnapshot snapshot)
    {
        lock (_readinessGate)
        {
            _latestReadiness = snapshot;
            if (_readinessSettled)
            {
                Enqueue(new ReadinessFrame(snapshot.Volumes));
            }
        }
    }

    void PublishSettledReadiness()
    {
        if (source is not IVolumeReadiness readiness)
        {
            return;
        }

        // Read outside the gate: the source raises its event under its own lock, so taking the
        // gate while holding the source's lock (and the reverse) must not both happen.
        var current = readiness.Readiness;
        lock (_readinessGate)
        {
            _readinessSettled = true;
            Enqueue(new ReadinessFrame((_latestReadiness ?? current).Volumes));
        }
    }
    void OnDriveFailed(string volume, string message) => Enqueue(new DriveFailedFrame(volume, message));

    void Enqueue(LinuxWatchHelperFrame frame) => _outbound.Writer.TryWrite(LinuxWatchHelperProtocol.Serialize(frame));

    volatile bool _sourceDeathSelected;

    void Finish(int exitCode) => _finished.TrySetResult(exitCode);

    // Reports the end of the watch once, unless the session is already ending for another reason.
    void Die(string reason)
    {
        if (_finished.Task.IsCompleted)
        {
            return;
        }

        // Recorded before the frame is queued: the writer may fail on that very frame, and the
        // first stop reason must still select the exit code.
        _sourceDeathSelected = true;
        Enqueue(new DiedFrame(reason));
        Finish(LinuxWatchHelperHost.ExitCodeSourceEnded);
    }

    async Task WriteOutboundAsync()
    {
        try
        {
            await foreach (var line in _outbound.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var bytes = Utf8WithoutMarker.GetBytes(line + "\n");
                await output.WriteAsync(bytes).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or NotSupportedException)
        {
            // The reader is gone: nobody can be told anything more, so the session ends now.
            Finish(_sourceDeathSelected ? LinuxWatchHelperHost.ExitCodeSourceEnded : LinuxWatchHelperHost.ExitCodeOutputFailed);
        }
    }

    async Task ReadRequestsAsync(Stream input)
    {
        Exception? failure = null;
        try
        {
            using var reader = new StreamReader(input, Utf8WithoutMarker, false, 4096, leaveOpen: true);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_requestGate)
                {
                    if (!_finished.Task.IsCompleted)
                    {
                        HandleRequest(line);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // An unreadable stdin is treated like its end: the parent can no longer reach the helper.
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Finish(LinuxWatchHelperHost.ExitCodeEndOfInput);
        if (failure is not null)
        {
            await diagnostics.WriteLineAsync($"Reading helper requests failed: {failure}").ConfigureAwait(false);
        }
    }

    void HandleRequest(string line)
    {
        if (!LinuxWatchHelperProtocol.TryParse(line, out var frame, out var error))
        {
            Enqueue(new ErrorFrame($"Rejected request: {error}"));
            return;
        }

        switch (frame)
        {
            case ArmRequestFrame arm:
                StartArm(arm.Roots);
                break;
            case WatchRequestFrame:
                StartWatch();
                break;
            default:
                Enqueue(new ErrorFrame($"Rejected request: {frame.GetType().Name} is not a request."));
                break;
        }
    }

    void StartArm(IReadOnlyList<string> roots)
    {
        if (_armTask is not null)
        {
            Enqueue(new ErrorFrame("Rejected request: the watch was already armed."));
            return;
        }

        if (roots.Count == 0 || roots.Any(root =>
                !Path.IsPathFullyQualified(root) && !root.StartsWith('/') || root.Contains('\0', StringComparison.Ordinal)))
        {
            Enqueue(new ErrorFrame("Rejected request: an arm request needs one or more absolute root paths."));
            return;
        }

        _roots = [.. roots.Select(WatchPath.Parse)];
        _rootParents = [.. roots.Select(Path.GetDirectoryName).OfType<string>().Select(WatchPath.Parse)];
        _armTask = Task.Run(() => ArmAsync(roots), _stop.Token);
    }

    async Task ArmAsync(IReadOnlyList<string> roots)
    {
        try
        {
            var result = await source.ArmAndCatchUpAsync(roots, _stop.Token).ConfigureAwait(false);
            _armed = true;
            PublishSettledReadiness();
            Enqueue(new ArmResultFrame(result.Errors));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The session is ending; there is nobody to report the arm to.
        }
        catch (Exception exception)
        {
            await diagnostics.WriteLineAsync($"Arming the watch failed: {exception}").ConfigureAwait(false);
            Die("The elevated Live helper could not arm the watch.");
        }
    }

    void StartWatch()
    {
        if (!_armed)
        {
            Enqueue(new ErrorFrame("Rejected request: watch was requested before the arm completed."));
            return;
        }

        if (_watchTask is not null)
        {
            Enqueue(new ErrorFrame("Rejected request: the watch is already running."));
            return;
        }

        _watchTask = WatchAsync();
    }

    async Task WatchAsync()
    {
        try
        {
            var enumerator = source.WatchAsync(_stop.Token).GetAsyncEnumerator(_stop.Token);
            await using (enumerator.ConfigureAwait(false))
            {
                // The first MoveNextAsync starts the pump; a source with a pump signal reports it
                // only once the pump really runs.
                var next = enumerator.MoveNextAsync();
                if (source is IPumpLifecycle lifecycle)
                {
                    _ = lifecycle.PumpStarted.ContinueWith(_ => Enqueue(new PumpStartedFrame()), CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                else
                {
                    Enqueue(new PumpStartedFrame());
                }

                while (await next.ConfigureAwait(false))
                {
                    if (FilterToRoots(enumerator.Current) is { } batch)
                    {
                        Enqueue(new BatchFrame(batch));
                    }

                    next = enumerator.MoveNextAsync();
                }
            }

            Die("The elevated watch ended.");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The session is ending: stdin closed, the output failed, or the source died.
        }
        catch (Exception exception)
        {
            await diagnostics.WriteLineAsync($"The watch failed: {exception}").ConfigureAwait(false);
            Die("The elevated watch failed.");
        }
    }

    // A filesystem mark reports every change on the filesystem. Only entries at or under an armed
    // root, or beside one (a root renamed or replaced within its parent), can affect the watch, so
    // nothing else crosses from root to the unprivileged parent.
    VolumeChangeBatch? FilterToRoots(VolumeChangeBatch batch)
    {
        var entries = (VolumeChangeEntry[])[.. batch.Entries.Where(entry => IsRelevant(entry.FullPath))];
        return entries.Length == 0 ? null : batch with { Entries = entries };
    }

    bool IsRelevant(string fullPath)
    {
        var path = WatchPath.Parse(fullPath);
        if (_roots.Any(path.IsUnderOrEqual))
        {
            return true;
        }

        return Path.GetDirectoryName(fullPath) is { } directory &&
               _rootParents.Any(WatchPath.Parse(directory).IsEqualTo);
    }

    static async Task ObserveAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected while the session stops.
        }
    }
}
