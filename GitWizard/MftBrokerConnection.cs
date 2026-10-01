using MFTLib;

namespace GitWizard;

/// <summary>
/// Lazily connects one broker process and owns whichever process it created, including a process
/// whose connection completed after the session opened or after its caller was cancelled.
/// A caller's token only abandons that caller's wait: the shared connection is cancelled by
/// disposal alone, so one cancelled caller never fails a concurrent or later one.
/// </summary>
internal sealed class MftBrokerConnection : IAsyncDisposable
{
    readonly Func<CancellationToken, Task<BrokerProcess>> _connectAsync;
    readonly Lock _gate = new();
    readonly CancellationTokenSource _stopping = new();
    Task<BrokerProcess>? _connection;
    Task? _disposal;
    BrokerProcess? _client;
    Task _endObservation = Task.CompletedTask;
    bool _disposed;
    int _faulted;

    public MftBrokerConnection(Func<CancellationToken, Task<BrokerProcess>> connectAsync)
    {
        ArgumentNullException.ThrowIfNull(connectAsync);
        _connectAsync = connectAsync;
    }

    public BrokerProcess? Process
    {
        get
        {
            lock (_gate)
                return _client;
        }
    }

    /// <summary>
    /// True once the owned process ended while this holder was live. Reads the process's end task
    /// directly, so an end that has completed is visible before the end observer's continuation runs.
    /// </summary>
    public bool IsFaulted
    {
        get
        {
            if (Volatile.Read(ref _faulted) != 0)
                return true;
            lock (_gate)
            {
                if (_disposed || _client is null || !_client.Ended.IsCompleted)
                    return false;
                Volatile.Write(ref _faulted, 1);
                return true;
            }
        }
    }

    public Task<BrokerProcess> ConnectAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _connection ??= ConnectCoreAsync(_stopping.Token);
            return _connection.WaitAsync(cancellationToken);
        }
    }

    public event Action? Connecting;
    public event Action? Connected;

    async Task<BrokerProcess> ConnectCoreAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        stoppingToken.ThrowIfCancellationRequested();
        Connecting?.Invoke();
        var process = await _connectAsync(stoppingToken).ConfigureAwait(false);
        Connected?.Invoke();
        bool abandoned;
        lock (_gate)
        {
            _client = process;
            _endObservation = ObserveEndAsync(process);
            abandoned = _disposed;
        }
        // Publish ownership before observing cancellation, because a launcher can return late.
        // Disposal marks the holder synchronously, so a process returned after it began is never handed out.
        if (abandoned)
            throw new OperationCanceledException("The broker connection was disposed while connecting.");
        return process;
    }

    async Task ObserveEndAsync(BrokerProcess process)
    {
        await process.Ended.ConfigureAwait(false);
        lock (_gate)
        {
            if (!_disposed)
                Volatile.Write(ref _faulted, 1);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            return new ValueTask(_disposal ??= DisposeCoreAsync(_connection));
        }
    }

    async Task DisposeCoreAsync(Task<BrokerProcess>? pending)
    {
        await Task.Yield();
        try
        {
            // Only an in-flight connection is abandoned; an established process's token stays
            // uncancelled so its orderly shutdown below is not mistaken for an abort.
            if (pending is not { IsCompletedSuccessfully: true })
                await _stopping.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await ObserveConnectionAsync(pending).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    var process = Process;
                    if (process is not null)
                    {
                        await process.DisposeAsync().ConfigureAwait(false);
                        await _endObservation.ConfigureAwait(false);
                    }
                }
                finally
                {
                    _stopping.Dispose();
                }
            }
        }
    }

    static async Task ObserveConnectionAsync(Task<BrokerProcess>? pending)
    {
        if (pending is null)
            return;
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception) when (pending.IsFaulted || pending.IsCanceled)
        {
            // ConnectAsync's caller owns the failure; cleanup only waits so a late process is reclaimed.
        }
    }
}
