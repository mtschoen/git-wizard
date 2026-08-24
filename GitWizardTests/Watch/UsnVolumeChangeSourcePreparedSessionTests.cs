using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using GitWizard.Watch;
using MFTLib;
using MFTLibTestExtensions;

namespace GitWizardTests.Watch;

#pragma warning disable CA1416 // Testing Windows-targeted types with in-memory streams across platforms

[TestFixture]
public class UsnVolumeChangeSourcePreparedSessionTests
{
    sealed class EmptyMmfReader : IMmfReader
    {
        public ScanRecord[] Read(string mmfName, long byteLength) => [];
    }

    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }

    sealed class DuplexStream : Stream
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

    static async Task<BrokerFrame> ReadOneFrameAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        await stream.ReadExactlyAsync(frameBytes.AsMemory(4, totalLength));
        return BrokerProtocol.ReadFrame(frameBytes, out _);
    }

    static async Task<(JournalBrokerScanSession Session, DuplexStream ServerSide)> CreateParkedSessionAsync(
        IReadOnlyList<string> drives,
        UsnJournalEntry[]? catchUpEntries = null,
        Dictionary<string, string>? errors = null)
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = new JournalBrokerClient(
            pipe: clientSide,
            mmfReader: new EmptyMmfReader(),
            createDriveMmf: (letter, _) => ($"gw-test-{letter}", NoOpDisposable.Instance));

        var brokerTask = Task.Run(async () =>
        {
            _ = await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();

            foreach (var drive in drives)
            {
                var letter = drive.TrimEnd(':', '\\', '/').ToUpperInvariant();
                if (errors != null && errors.TryGetValue(letter, out var error))
                {
                    BrokerProtocol.WriteError(response, letter, error);
                }
                else
                {
                    BrokerProtocol.WriteCursor(response, letter, new UsnJournalCursor(100UL, 1000L));
                    BrokerProtocol.WriteScanReady(response, $"gw-test-{letter}", 0, 0);
                    BrokerProtocol.WriteJournalBatch(response, letter, new UsnJournalCursor(100UL, 1000L), catchUpEntries ?? []);
                }
            }

            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await ScanSessionTestHarness.StartScannedAsync(
            _ => Task.FromResult(client), drives,
            BrokerScanProfile.DirectoryIndex, ["git"]);
        await brokerTask;

        return (session, serverSide);
    }

    [Test]
    public async Task ArmAndCatchUpAsync_PreparedSession_MatchingVolumes_AdoptsSessionWithoutCallingBrokerLaunch()
    {
        var (session, serverSide) = await CreateParkedSessionAsync(["C:\\"]);
        var scanRecords = new List<ScanRecord>
        {
            new(RecordNumber: 42, ParentRecordNumber: 0, 0, 0, 0, IsDirectory: true, Name: "repo", Path: "C:\\code\\repo")
        };

        var brokerLaunchCalled = false;
        await using var source = new UsnVolumeChangeSource(
            session, scanRecords, _ => { brokerLaunchCalled = true; return true; }, ["C"]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var armResult = await source.ArmAndCatchUpAsync(["C"], cts.Token);

        Assert.That(brokerLaunchCalled, Is.False);
        Assert.That(armResult.Errors, Is.Empty);
        Assert.That(session.State, Is.EqualTo(JournalBrokerSessionState.Parked));

        serverSide.Dispose();
    }

    [Test]
    public async Task ArmAndCatchUpAsync_PreparedSession_VolumeMismatch_DisposesPreparedSessionAndFallsBack()
    {
        var (session, serverSide) = await CreateParkedSessionAsync(["C:\\"]);
        IReadOnlyList<ScanRecord> scanRecords = [];

        var brokerLaunchCalled = false;
        await using var source = new UsnVolumeChangeSource(
            session, scanRecords, _ =>
            {
                brokerLaunchCalled = true;
                throw new InvalidOperationException("Broker spawn failed on fallback");
            }, ["C"]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await source.ArmAndCatchUpAsync(["C", "D"], cts.Token));

        Assert.That(brokerLaunchCalled, Is.True);
        Assert.That(session.State, Is.EqualTo(JournalBrokerSessionState.Disposed));

        serverSide.Dispose();
    }

    [Test]
    public async Task ArmAndCatchUpAsync_PreparedSession_FaultedSession_DisposesPreparedSessionAndFallsBack()
    {
        var (session, serverSide) = await CreateParkedSessionAsync(["C:\\"]);
        // Force session into disposed/faulted state
        await session.DisposeAsync();

        var brokerLaunchCalled = false;
        await using var source = new UsnVolumeChangeSource(
            session, [], _ =>
            {
                brokerLaunchCalled = true;
                throw new InvalidOperationException("Broker fallback called");
            }, ["C"]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await source.ArmAndCatchUpAsync(["C"], cts.Token));

        Assert.That(brokerLaunchCalled, Is.True);

        serverSide.Dispose();
    }

    [Test]
    public async Task DisposeAsync_PreparedSession_NeverArmed_DisposesPreparedSession()
    {
        var (session, serverSide) = await CreateParkedSessionAsync(["C:\\"]);

        var source = new UsnVolumeChangeSource(session, [], _ => true, ["C"]);
        await source.DisposeAsync();

        Assert.That(session.State, Is.EqualTo(JournalBrokerSessionState.Disposed));

        serverSide.Dispose();
    }

    [Test]
    public async Task WatchAsync_PreparedSession_ReplaysCatchUpAndResolvesPaths()
    {
        var catchUpEntry = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 42,
            ParentRecordNumber = 0,
            Usn = 1050L,
            Timestamp = DateTime.UtcNow,
            FileAttributes = FileAttributes.Directory,
            Reason = UsnReason.FileCreate | UsnReason.Close,
            FileName = "repo"
        });

        var (session, serverSide) = await CreateParkedSessionAsync(["C:\\"], [catchUpEntry]);
        var scanRecords = new List<ScanRecord>
        {
            new(RecordNumber: 42, ParentRecordNumber: 0, 0, 0, 0, IsDirectory: true, Name: "repo", Path: "C:\\code\\repo")
        };

        await using var source = new UsnVolumeChangeSource(session, scanRecords, _ => true, ["C"]);

        using var armCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await source.ArmAndCatchUpAsync(["C"], armCts.Token);

        var startWatchFrameTask = ReadOneFrameAsync(serverSide);

        using var watchCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var batches = new List<VolumeChangeBatch>();

        try
        {
            await foreach (var batch in source.WatchAsync(watchCts.Token))
            {
                batches.Add(batch);
                break;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected after reading catch-up batch
        }

        var startWatchFrame = await startWatchFrameTask;

        Assert.Multiple(() =>
        {
            Assert.That(startWatchFrame.Kind, Is.EqualTo(BrokerFrameKind.StartWatch));
            Assert.That(batches, Has.Count.EqualTo(1));
            Assert.That(batches[0].Volume, Is.EqualTo("C"));
            Assert.That(batches[0].Entries, Has.Count.EqualTo(1));
            Assert.That(batches[0].Entries[0].FullPath, Is.EqualTo("C:\\code\\repo"));
        });

        serverSide.Dispose();
    }
}
