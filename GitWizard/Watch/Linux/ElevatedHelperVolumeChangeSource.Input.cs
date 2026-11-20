namespace GitWizard.Watch.Linux;

// The helper-input half: request writes, closing stdin, and ending pkexec before it becomes root.
public sealed partial class ElevatedHelperVolumeChangeSource
{
    // A write the helper never reads holds the gate until it completes, but its caller returns on
    // cancellation and disposal, and disposal waits for the gate only for a bounded time.
    async Task SendAsync(LinuxWatchHelperFrame frame, CancellationToken ct)
    {
        if (_process is not { } process)
        {
            return;
        }

        var bytes = Utf8WithoutMarker.GetBytes(LinuxWatchHelperProtocol.Serialize(frame) + "\n");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposal.Token);
        await _inputGate.WaitAsync(linked.Token).ConfigureAwait(false);
        Task write;
        try
        {
            if (_inputClosed)
            {
                _inputGate.Release();
                return;
            }

            write = WriteAsync(process.Input, bytes, _disposal.Token);
        }
        catch
        {
            _inputGate.Release();
            throw;
        }

        _ = write.ContinueWith(_ => _inputGate.Release(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await write.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The helper is gone; the output reader reports the end.
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Disposal abandons the write; closing stdin follows.
        }
    }

    static async Task WriteAsync(Stream input, byte[] bytes, CancellationToken cancellationToken)
    {
        await input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task CloseInputAsync(LinuxWatchHelperProcess process)
    {
        var acquired = false;
        try
        {
            await _inputGate.WaitAsync().WaitAsync(_exitWait, _timeProvider).ConfigureAwait(false);
            acquired = true;
        }
        catch (TimeoutException)
        {
            GitWizardLog.Log("A request to the elevated Live helper is still being written; closing its input anyway.",
                GitWizardLog.LogType.Warning);
        }

        try
        {
            _inputClosed = true;
            await process.Input.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or NotSupportedException)
        {
            // Closing a pipe the helper already left still tells it nothing more is coming.
        }
        finally
        {
            if (acquired)
            {
                _inputGate.Release();
            }
        }
    }

    // Before hello the launched process is pkexec running as the caller, so it can be signalled;
    // ending it closes its authentication dialog. Once it has become root the signal is refused,
    // which the process seam swallows, and closing stdin ends the helper instead.
    void TerminateBeforeHello()
    {
        if (_process is not { } process || _helloReceived)
        {
            return;
        }

        _terminated = true;
        process.Terminate();
    }
}
