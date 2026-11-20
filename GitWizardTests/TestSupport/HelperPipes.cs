using System.IO.Pipelines;
using System.Text;
using GitWizard.Watch.Linux;

namespace GitWizardTests.TestSupport;

/// <summary>
/// In-memory stdin, stdout and stderr between a Linux watch helper client and its helper, with
/// the helper's exit code under test control. The client side is <see cref="Process"/>; the
/// helper side reads requests from <see cref="HelperInput"/> and writes frames to
/// <see cref="HelperOutput"/>.
/// </summary>
sealed class HelperPipes : IAsyncDisposable
{
    readonly Pipe _stdin = new();
    readonly Pipe _stdout = new();
    readonly Pipe _stderr = new();
    readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly StreamReader _requests;
    readonly StreamReader _frames;
    readonly StreamWriter _helperWriter;

    public HelperPipes()
    {
        HelperInput = _stdin.Reader.AsStream();
        HelperOutput = _stdout.Writer.AsStream();
        ClientInput = _stdin.Writer.AsStream();
        ClientOutput = _stdout.Reader.AsStream();
        Process = new LinuxWatchHelperProcess(ClientInput, ClientOutput, _stderr.Reader.AsStream(), _exit.Task);
        _requests = new StreamReader(HelperInput, Encoding.UTF8);
        _frames = new StreamReader(ClientOutput, Encoding.UTF8);
        _helperWriter = new StreamWriter(HelperOutput, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public LinuxWatchHelperProcess Process { get; }
    public Stream HelperInput { get; }
    public Stream HelperOutput { get; }
    public Stream ClientInput { get; }
    public Stream ClientOutput { get; }

    /// <summary>Writes one frame as the helper.</summary>
    public Task SendFrameAsync(LinuxWatchHelperFrame frame) => SendLineAsync(LinuxWatchHelperProtocol.Serialize(frame));

    public Task SendLineAsync(string line) => _helperWriter.WriteLineAsync(line).WaitAsync(TestTimeouts.ShortHangGuard);

    /// <summary>Writes one raw request line as the client.</summary>
    public async Task SendRequestLineAsync(string line)
    {
        await ClientInput.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")).AsTask().WaitAsync(TestTimeouts.ShortHangGuard);
        await ClientInput.FlushAsync().WaitAsync(TestTimeouts.ShortHangGuard);
    }

    public Task SendRequestAsync(LinuxWatchHelperFrame frame) => SendRequestLineAsync(LinuxWatchHelperProtocol.Serialize(frame));

    /// <summary>Reads the next request the client wrote, or null at the end of its stdin.</summary>
    public async Task<LinuxWatchHelperFrame?> ReadRequestAsync() => Parse(await _requests.ReadLineAsync().WaitAsync(TestTimeouts.ShortHangGuard));

    /// <summary>Reads the next frame the helper wrote, or null at the end of its stdout.</summary>
    public async Task<LinuxWatchHelperFrame?> ReadFrameAsync() => Parse(await _frames.ReadLineAsync().WaitAsync(TestTimeouts.ShortHangGuard));

    /// <summary>Reads frames until one of type <typeparamref name="TFrame"/>, returning it and those before it.</summary>
    public async Task<(TFrame Frame, List<LinuxWatchHelperFrame> Before)> ReadFrameAsync<TFrame>() where TFrame : LinuxWatchHelperFrame
    {
        var before = new List<LinuxWatchHelperFrame>();
        while (true)
        {
            var frame = await ReadFrameAsync() ?? throw new AssertionException($"The helper output ended before a {typeof(TFrame).Name}.");
            if (frame is TFrame match)
            {
                return (match, before);
            }

            before.Add(frame);
        }
    }

    /// <summary>Ends the helper: its stdout and stderr close, then it exits with <paramref name="exitCode"/>.</summary>
    public async Task ExitAsync(int exitCode)
    {
        await _stdout.Writer.CompleteAsync();
        await _stderr.Writer.CompleteAsync();
        _exit.TrySetResult(exitCode);
    }

    /// <summary>Closes the client's end of stdin, as the client's disposal does.</summary>
    public Task CloseClientInputAsync() => _stdin.Writer.CompleteAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        await _stdin.Writer.CompleteAsync();
        await _stdout.Writer.CompleteAsync();
        await _stderr.Writer.CompleteAsync();
        _exit.TrySetResult(0);
        _requests.Dispose();
        _frames.Dispose();
    }

    static LinuxWatchHelperFrame? Parse(string? line)
    {
        if (line is null)
        {
            return null;
        }

        Assert.That(LinuxWatchHelperProtocol.TryParse(line, out var frame, out var error), Is.True, $"{error}: {line}");
        return frame;
    }
}
