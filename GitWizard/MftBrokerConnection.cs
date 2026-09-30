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

    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

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
            process.Ended += OnBrokerEnded;
            abandoned = _disposed;
        }
        // Ended fires once and is not replayed, so a process that ended before the subscription
        // latches the fault here instead.
        if (process.HasEnded)
            Volatile.Write(ref _faulted, 1);
        // Publish ownership before observing cancellation, because a launcher can return late.
        // Disposal marks the holder synchronously, so a process returned after it began is never handed out.
        if (abandoned)
            throw new OperationCanceledException("The broker connection was disposed while connecting.");
        return process;
    }

    void OnBrokerEnded(string reason) => Volatile.Write(ref _faulted, 1);

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
                        process.Ended -= OnBrokerEnded;
                        await process.DisposeAsync().ConfigureAwait(false);
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
