using MFTLib;

namespace GitWizard;

/// <summary>
/// Lazily connects one broker client and owns whichever client it created, including a client
/// whose connection completed after the session opened or after its caller was cancelled.
/// A caller's token only abandons that caller's wait: the shared connection is cancelled by
/// disposal alone, so one cancelled caller never fails a concurrent or later one.
/// </summary>
internal sealed class MftBrokerConnection : IAsyncDisposable
{
    readonly Func<CancellationToken, Task<JournalBrokerClient>> _connectAsync;
    readonly Lock _gate = new();
    readonly CancellationTokenSource _stopping = new();
    Task<JournalBrokerClient>? _connection;
    Task? _disposal;
    JournalBrokerClient? _client;
    bool _disposed;
    int _faulted;

    public MftBrokerConnection(Func<CancellationToken, Task<JournalBrokerClient>> connectAsync)
    {
        ArgumentNullException.ThrowIfNull(connectAsync);
        _connectAsync = connectAsync;
    }

    public JournalBrokerClient? Client
    {
        get
        {
            lock (_gate)
                return _client;
        }
    }

    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    public Task<JournalBrokerClient> ConnectAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _connection ??= ConnectCoreAsync(_stopping.Token);
            return _connection.WaitAsync(cancellationToken);
        }
    }

    async Task<JournalBrokerClient> ConnectCoreAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        stoppingToken.ThrowIfCancellationRequested();
        var client = await _connectAsync(stoppingToken).ConfigureAwait(false);
        bool abandoned;
        lock (_gate)
        {
            _client = client;
            client.BrokerDied += OnBrokerDied;
            abandoned = _disposed;
        }
        // Publish ownership before observing cancellation, because a launcher can return late.
        // Disposal marks the holder synchronously, so a client returned after it began is never handed out.
        if (abandoned)
            throw new OperationCanceledException("The broker connection was disposed while connecting.");
        return client;
    }

    void OnBrokerDied(string reason) => Volatile.Write(ref _faulted, 1);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            return new ValueTask(_disposal ??= DisposeCoreAsync(_connection));
        }
    }

    async Task DisposeCoreAsync(Task<JournalBrokerClient>? pending)
    {
        await Task.Yield();
        try
        {
            // Only an in-flight connection is abandoned; an established client's token stays
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
                    var client = Client;
                    if (client is not null)
                    {
                        client.BrokerDied -= OnBrokerDied;
                        await client.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    _stopping.Dispose();
                }
            }
        }
    }

    static async Task ObserveConnectionAsync(Task<JournalBrokerClient>? pending)
    {
        if (pending is null)
            return;
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception) when (pending.IsFaulted || pending.IsCanceled)
        {
            // ConnectAsync's caller owns the failure; cleanup only waits so a late client is reclaimed.
        }
    }
}
