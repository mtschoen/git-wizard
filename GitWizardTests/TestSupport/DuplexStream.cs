using System.IO.Pipelines;

namespace GitWizardTests.TestSupport;

internal sealed class DuplexStream : Stream
{
    readonly Stream _read;
    readonly Stream _write;

    DuplexStream(Stream read, Stream write)
    {
        _read = read;
        _write = write;
    }

    public static (DuplexStream Client, DuplexStream Server) CreatePair()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var client = new DuplexStream(serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream());
        var server = new DuplexStream(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        return (client, server);
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _write.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _read.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _write.WriteAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _read.Dispose();
            _write.Dispose();
        }
        base.Dispose(disposing);
    }
}

