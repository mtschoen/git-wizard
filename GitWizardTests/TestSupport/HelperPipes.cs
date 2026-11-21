using System.IO.Pipelines;
using System.Text;
using GitWizard.Watch.Linux;

namespace GitWizardTests.TestSupport;

/// <summary>
/// In-memory stdin, stdout and stderr between a Linux watch helper client and its helper, with
/// the helper's exit code under test control. The client side is <see cref="Process"/>; the
/// helper side reads requests from <see cref="HelperInput"/> and writes frames to
/// <see cref="HelperOutput"/>. <see cref="LinuxWatchHelperProcess.Terminate"/> is recorded; by
/// default it ends the fake as a signalled pkexec would (exit 143).
/// </summary>
sealed class HelperPipes : IAsyncDisposable
{
    public const int TerminatedExitCode = 143;

    /// <summary>A client stdin whose writes stay pending, ignoring cancellation, until released.</summary>
    sealed class HeldWriteStream(Stream inner, Task release, TaskCompletionSource writeStarted) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(CancellationToken.None);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            writeStarted.TrySetResult();
            await release;
            await inner.WriteAsync(buffer, CancellationToken.None);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Reports the client's stdin closing, as a helper sees end of input and exits.</summary>
    sealed class CloseNotifyingStream(Stream inner, Action closed) : Stream
    {
        int _closeCount;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                if (Interlocked.Increment(ref _closeCount) == 1)
                {
                    closed();
                }
            }

            base.Dispose(disposing);
        }
    }

    readonly Pipe _stdin = new();
    readonly Pipe _stdout = new();
    readonly Pipe _stderr = new();
    readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly StreamReader _requests;
    readonly StreamReader _frames;
    readonly StreamWriter _helperWriter;
    readonly TaskCompletionSource _releaseWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _clientWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _terminateCount;

    public HelperPipes(bool holdClientWrites = false)
    {
        HelperInput = _stdin.Reader.AsStream();
        HelperOutput = _stdout.Writer.AsStream();
        var clientInput = holdClientWrites ? new HeldWriteStream(_stdin.Writer.AsStream(), _releaseWrites.Task, _clientWriteStarted) : _stdin.Writer.AsStream();
        ClientInput = new CloseNotifyingStream(clientInput, OnClientInputClosed);
        ClientOutput = _stdout.Reader.AsStream();
        Process = new LinuxWatchHelperProcess(ClientInput, ClientOutput, _stderr.Reader.AsStream(), _exit.Task, OnTerminate);
        _requests = new StreamReader(HelperInput, Encoding.UTF8);
        _frames = new StreamReader(ClientOutput, Encoding.UTF8);
        _helperWriter = new StreamWriter(HelperOutput, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public LinuxWatchHelperProcess Process { get; }
    public int TerminateCount => Volatile.Read(ref _terminateCount);

    /// <summary>False models a process that already became root: the signal is refused and nothing ends.</summary>
    public bool ExitOnTerminate { get; init; } = true;

    public void ReleaseWrites() => _releaseWrites.TrySetResult();

    /// <summary>Completes when a held client write has begun.</summary>
    public Task ClientWriteStarted => _clientWriteStarted.Task;

    /// <summary>False models a helper that ignores end of input and stays alive until terminated.</summary>
    public bool ExitWhenClientInputCloses { get; init; } = true;

    void OnClientInputClosed()
    {
        if (ExitWhenClientInputCloses)
        {
            _ = ExitAsync(0);
        }
    }

    void OnTerminate()
    {
        Interlocked.Increment(ref _terminateCount);
        if (ExitOnTerminate)
        {
            _ = ExitAsync(TerminatedExitCode);
        }
    }
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
        _releaseWrites.TrySetResult();
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
