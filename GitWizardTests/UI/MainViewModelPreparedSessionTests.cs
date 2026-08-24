using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using GitWizard;
using GitWizard.Watch;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;
using MFTLib;
using MFTLibTestExtensions;

namespace GitWizardTests.UI;

#pragma warning disable CA1416 // Testing Windows-targeted types with in-memory streams across platforms

[TestFixture]
public class MainViewModelPreparedSessionTests
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
        IReadOnlyList<string> drives)
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
                BrokerProtocol.WriteCursor(response, letter, new UsnJournalCursor(100UL, 1000L));
                BrokerProtocol.WriteScanReady(response, $"gw-test-{letter}", 0, 0);
                BrokerProtocol.WriteJournalBatch(response, letter, new UsnJournalCursor(100UL, 1000L), []);
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

    static MainViewModel NewViewModel()
        => new(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public async Task SetPreparedDiscoverySessionAsync_ReplacesAndDisposesOldSession()
    {
        var (session1, serverSide1) = await CreateParkedSessionAsync(["C:\\"]);
        var (session2, serverSide2) = await CreateParkedSessionAsync(["C:\\"]);

        await using var viewModel = NewViewModel();

        await viewModel.SetPreparedDiscoverySessionAsync(
            new PreparedDiscoverySession(session1, []));
        Assert.That(viewModel.PreparedDiscoverySession, Is.Not.Null);

        await viewModel.SetPreparedDiscoverySessionAsync(
            new PreparedDiscoverySession(session2, []));
        Assert.That(viewModel.PreparedDiscoverySession?.Session, Is.SameAs(session2));
        Assert.That(session1.State, Is.EqualTo(JournalBrokerSessionState.Disposed));
        Assert.That(session2.State, Is.EqualTo(JournalBrokerSessionState.Parked));

        serverSide1.Dispose();
        serverSide2.Dispose();
    }

    [Test]
    public async Task ClearCacheAsync_DisposesPreparedDiscoverySession()
    {
        var (session, serverSide) = await CreateParkedSessionAsync(["C:\\"]);
        await using var viewModel = NewViewModel();

        await viewModel.SetPreparedDiscoverySessionAsync(
            new PreparedDiscoverySession(session, []));
        Assert.That(viewModel.PreparedDiscoverySession, Is.Not.Null);

        await viewModel.ClearCacheAsync();

        Assert.That(viewModel.PreparedDiscoverySession, Is.Null);
        Assert.That(session.State, Is.EqualTo(JournalBrokerSessionState.Disposed));

        serverSide.Dispose();
    }

    [Test]
    public async Task DeleteAllLocalFilesAsync_DisposesPreparedDiscoverySession()
    {
        var (session, serverSide) = await CreateParkedSessionAsync(["C:\\"]);
        await using var viewModel = NewViewModel();

        await viewModel.SetPreparedDiscoverySessionAsync(
            new PreparedDiscoverySession(session, []));
        Assert.That(viewModel.PreparedDiscoverySession, Is.Not.Null);

        await viewModel.DeleteAllLocalFilesAsync();

        Assert.That(viewModel.PreparedDiscoverySession, Is.Null);
        Assert.That(session.State, Is.EqualTo(JournalBrokerSessionState.Disposed));

        serverSide.Dispose();
    }

    [Test]
    public async Task DisposeAsync_DisposesPreparedDiscoverySession()
    {
        var (session, serverSide) = await CreateParkedSessionAsync(["C:\\"]);
        var viewModel = NewViewModel();

        await viewModel.SetPreparedDiscoverySessionAsync(
            new PreparedDiscoverySession(session, []));
        Assert.That(viewModel.PreparedDiscoverySession, Is.Not.Null);

        await viewModel.DisposeAsync();

        Assert.That(viewModel.PreparedDiscoverySession, Is.Null);
        Assert.That(session.State, Is.EqualTo(JournalBrokerSessionState.Disposed));

        serverSide.Dispose();
    }

    [Test]
    public void TakePreparedDiscoverySession_AtomicallyTransfersAndClears()
    {
        var viewModel = NewViewModel();
        Assert.That(viewModel.TakePreparedDiscoverySession(), Is.Null);
    }

    [Test]
    public async Task PreparedDiscoverySession_PreservesDrives()
    {
        var (session, serverSide) = await CreateParkedSessionAsync(["C:\\"]);
        IReadOnlyList<string> drives = ["C"];
        var prepared = new PreparedDiscoverySession(session, [], drives);

        Assert.Multiple(() =>
        {
            Assert.That(prepared.Session, Is.SameAs(session));
            Assert.That(prepared.ScanRecords, Is.Empty);
            Assert.That(prepared.Drives, Is.EqualTo(drives));
        });

        await prepared.DisposeAsync();
        serverSide.Dispose();
    }

    [Test]
    public async Task SetPreparedDiscoverySessionAsync_WhenSessionNull_DisposesExistingSession()
    {
        var (session, serverSide) = await CreateParkedSessionAsync(["C:\\"]);
        await using var viewModel = NewViewModel();

        await viewModel.SetPreparedDiscoverySessionAsync(
            new PreparedDiscoverySession(session, []));
        Assert.That(viewModel.PreparedDiscoverySession, Is.Not.Null);

        await viewModel.SetPreparedDiscoverySessionAsync(null);

        Assert.That(viewModel.PreparedDiscoverySession, Is.Null);
        Assert.That(session.State, Is.EqualTo(JournalBrokerSessionState.Disposed));

        serverSide.Dispose();
    }

    [Test]
    public async Task ToggleLiveAsync_TwoConsecutiveStarts_FirstAdoptsPreparedSession_SecondLaunchesBroker()
    {
        var (session, serverSide) = await CreateParkedSessionAsync(["C:\\"]);
        IReadOnlyList<ScanRecord> scanRecords =
        [
            new(RecordNumber: 42, ParentRecordNumber: 0, 0, 0, 0, IsDirectory: true, Name: "repo", Path: "C:\\code\\repo")
        ];
        IReadOnlyList<string> drives = ["C"];

        var brokerLaunchCount = 0;
        bool FakeBrokerLaunch(string _)
        {
            brokerLaunchCount++;
            return false;
        }

        var viewModel = NewViewModel();
        await viewModel.SetPreparedDiscoverySessionAsync(
            new PreparedDiscoverySession(session, scanRecords, drives));

        viewModel.LiveVolumeChangeSourceFactory = () =>
        {
            var prepared = viewModel.TakePreparedDiscoverySession();
            if (prepared != null)
                return new UsnVolumeChangeSource(prepared.Session, prepared.ScanRecords, FakeBrokerLaunch, prepared.Drives);

            return new UsnVolumeChangeSource(FakeBrokerLaunch);
        };
        viewModel.LiveIsElevated = () => false;
        viewModel.AddRepository(new GitWizardRepository(@"C:\code\repo"));

        // First start: adopts prepared session with zero broker launches
        var startTask1 = viewModel.ToggleLiveAsync();
        var startWatchFrame = await ReadOneFrameAsync(serverSide);
        Assert.That(startWatchFrame.Kind, Is.EqualTo(BrokerFrameKind.StartWatch));

        await WaitUntilAsync(() => viewModel.IsLive);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.True);
            Assert.That(brokerLaunchCount, Is.EqualTo(0), "First start must adopt prepared session with zero broker launches.");
            Assert.That(viewModel.PreparedDiscoverySession, Is.Null, "Prepared discovery session must be consumed.");
        });

        // Stop first start: disposes the adopted prepared session
        await viewModel.ToggleLiveAsync();
        await startTask1;

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(session.State, Is.EqualTo(JournalBrokerSessionState.Disposed), "Prepared session must be disposed after stopping.");
        });

        // Second start: falls through to a fresh UsnVolumeChangeSource and launches broker
        await viewModel.ToggleLiveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.False, "Second start fails cleanly because fake broker launcher returned false.");
            Assert.That(brokerLaunchCount, Is.EqualTo(1), "Second start must launch exactly one broker.");
            Assert.That(session.State, Is.EqualTo(JournalBrokerSessionState.Disposed), "Prepared session was disposed exactly once.");
        });

        serverSide.Dispose();
    }

    [Test]
    public async Task ToggleLiveAsync_ElevatedRespawn_FirstAdoptsPreparedSession_RespawnLaunchesBroker()
    {
        var (session, serverSide) = await CreateParkedSessionAsync(["C:\\"]);
        IReadOnlyList<ScanRecord> scanRecords =
        [
            new(RecordNumber: 42, ParentRecordNumber: 0, 0, 0, 0, IsDirectory: true, Name: "repo", Path: "C:\\code\\repo")
        ];
        IReadOnlyList<string> drives = ["C"];

        var brokerLaunchCount = 0;
        bool FakeBrokerLaunch(string _)
        {
            brokerLaunchCount++;
            return false;
        }

        var viewModel = NewViewModel();
        await viewModel.SetPreparedDiscoverySessionAsync(
            new PreparedDiscoverySession(session, scanRecords, drives));

        viewModel.LiveVolumeChangeSourceFactory = () =>
        {
            var prepared = viewModel.TakePreparedDiscoverySession();
            if (prepared != null)
                return new UsnVolumeChangeSource(prepared.Session, prepared.ScanRecords, FakeBrokerLaunch, prepared.Drives);

            return new UsnVolumeChangeSource(FakeBrokerLaunch);
        };
        viewModel.LiveIsElevated = () => true;
        viewModel.AddRepository(new GitWizardRepository(@"C:\code\repo"));

        // First start: adopts prepared session with zero broker launches
        var startTask = viewModel.ToggleLiveAsync();
        var startWatchFrame = await ReadOneFrameAsync(serverSide);
        Assert.That(startWatchFrame.Kind, Is.EqualTo(BrokerFrameKind.StartWatch));

        await WaitUntilAsync(() => viewModel.IsLive);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.True);
            Assert.That(brokerLaunchCount, Is.EqualTo(0), "First start must adopt prepared session with zero broker launches.");
            Assert.That(viewModel.PreparedDiscoverySession, Is.Null, "Prepared discovery session must be consumed.");
        });

        // Kill the first source by disposing the server side stream
        serverSide.Dispose();

        // The controller disposes the dead source (disposing prepared session) and respawns,
        // which falls through to a fresh UsnVolumeChangeSource, launching exactly one broker.
        await startTask;

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(brokerLaunchCount, Is.EqualTo(1), "Respawn must launch exactly one broker.");
            Assert.That(session.State, Is.EqualTo(JournalBrokerSessionState.Disposed), "Prepared session was disposed exactly once.");
        });
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }
}
