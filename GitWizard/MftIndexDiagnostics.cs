using System.Threading.Channels;

namespace GitWizard;

/// <summary>
/// Relays MFTLib's synchronous index diagnostics to a sink on a worker, so the library's callback
/// only enqueues and never waits on logging. Best effort: at most 256 records are queued, the
/// oldest are dropped when full, and the drop count is reported once the queue drains.
/// </summary>
sealed class MftIndexDiagnostics : IAsyncDisposable
{
    const int Capacity = 256;
    readonly Channel<string> _messages;
    readonly Task _drain;
    long _dropped;

    public MftIndexDiagnostics(Action<string> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _messages = Channel.CreateBounded<string>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest
        }, _ => Interlocked.Increment(ref _dropped));
        _drain = Task.Run(() => DrainAsync(sink));
    }

    async Task DrainAsync(Action<string> sink)
    {
        await foreach (var message in _messages.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            sink(message);
        }

        var dropped = Interlocked.Read(ref _dropped);
        if (dropped != 0)
        {
            sink($"MFT index diagnostics dropped {dropped} records because the log queue was full.");
        }
    }

    /// <summary>A relay whose worker writes each record to <see cref="GitWizardLog" /> at its default Info level.</summary>
    public static MftIndexDiagnostics CreateForGitWizardLog() => new(message => GitWizardLog.Log(message));

    public void Report(string message) => _messages.Writer.TryWrite(message);

    /// <summary>Completes the queue and waits for it to drain; a sink failure surfaces here.</summary>
    public async ValueTask DisposeAsync()
    {
        _messages.Writer.TryComplete();
        await _drain.ConfigureAwait(false);
    }
}
