using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipelines;
using GitWizard;
using MFTLib;
using MFTLibTestExtensions;

namespace GitWizardTests;

/// <summary>
/// Covers the not-elevated MFT discovery path after its migration onto MFTLib's
/// journal broker: <see cref="GitWizardApi.TryFindAllRepositoriesUsingMftAsync"/> spawns
/// one elevated broker, takes a cold MFT scan, and pulls repository roots out of the
/// returned <see cref="ScanRecord"/>s - replacing the old <c>--elevated-mft</c> temp-file
/// roundtrip. The broker is injected as a scan seam so the whole flow is exercised without
/// real elevation; the filesystem checks are real, so these tests are Windows-only.
/// </summary>
public class BrokerDiscoveryTests
{
    sealed class FakeElevationProvider : IElevationProvider
    {
        public bool Elevated;
        public bool IsElevated() => Elevated;
        public bool CanSelfElevate() => true;
        public bool TryRunElevated(string arguments, int timeoutMs = 60000) => false;
    }

    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static string CreateRepoTree(out string repoA, out string repoB)
    {
        var root = Path.Combine(Path.GetTempPath(), $"gw-broker-{Guid.NewGuid():N}");
        repoA = Path.Combine(root, "repoA");
        repoB = Path.Combine(root, "nested", "repoB");
        Directory.CreateDirectory(Path.Combine(repoA, ".git"));
        Directory.CreateDirectory(Path.Combine(repoB, ".git"));
        return root;
    }

    static ScanRecord GitDirRecord(string repoPath) =>
        new(0, 0, 0, 0, 0, IsDirectory: true, Name: ".git", Path: Path.Combine(repoPath, ".git"));

    [Test]
    [Platform("Win")]
    public async Task TryFindAllRepositoriesUsingMftAsync_NotElevated_FindsGitReposFromBrokerScan()
    {
        var root = CreateRepoTree(out var repoA, out var repoB);
        try
        {
            var configuration = new GitWizardConfiguration();
            configuration.SearchPaths.Add(root);
            var paths = new SortedSet<string>();

            var scanned = new List<ScanRecord> { GitDirRecord(repoA), GitDirRecord(repoB) };

            var result = await GitWizardApi.TryFindAllRepositoriesUsingMftAsync(
                configuration, paths,
                elevation: new FakeElevationProvider { Elevated = false },
                scanProvider: _ => Task.FromResult<IReadOnlyList<ScanRecord>>(scanned));

            Assert.That(result, Is.True);
            Assert.That(paths, Has.Count.EqualTo(2));
            // NormalizePath lower-cases, so match case-insensitively.
            Assert.That(paths, Has.Some.EndsWith("repoA").IgnoreCase);
            Assert.That(paths, Has.Some.EndsWith("repoB").IgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Platform("Win")]
    public async Task TryFindAllRepositoriesUsingMftAsync_NotElevated_BrokerDeclined_ReturnsFalse()
    {
        var root = CreateRepoTree(out _, out _);
        try
        {
            var configuration = new GitWizardConfiguration();
            configuration.SearchPaths.Add(root);
            var paths = new SortedSet<string>();

            // A declined UAC / failed spawn surfaces as InvalidOperationException from the broker;
            // discovery must swallow it and report failure so the caller falls back to a scan.
            var result = await GitWizardApi.TryFindAllRepositoriesUsingMftAsync(
                configuration, paths,
                elevation: new FakeElevationProvider { Elevated = false },
                scanProvider: _ => throw new InvalidOperationException("UAC declined"));

            Assert.That(result, Is.False);
            Assert.That(paths, Is.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryFindAllRepositoriesUsingMftAsync_NoMft_ReturnsFalseWithoutScanning()
    {
        var configuration = new GitWizardConfiguration();
        configuration.SearchPaths.Add(Path.GetTempPath());
        var paths = new SortedSet<string>();
        var scanned = false;

        var result = await GitWizardApi.TryFindAllRepositoriesUsingMftAsync(
            configuration, paths, noMft: true,
            elevation: new FakeElevationProvider { Elevated = false },
            scanProvider: _ => { scanned = true; return Task.FromResult<IReadOnlyList<ScanRecord>>([]); });

        Assert.That(result, Is.False);
        Assert.That(scanned, Is.False);
    }

    [Test]
    [Platform("Win")]
    public async Task TryFindAllRepositoriesUsingMftAsync_NotElevated_DirectoryIndexPayload_FindsWorktreeGitFile()
    {
        // A DirectoryIndex-shaped payload: plain directory records plus a .git *file* record
        // (a worktree/submodule pointer). Under a scoped search path, discovery keeps .git
        // files, so the worktree root is found while the plain directory produces no false
        // positive. Only the parent directories need to exist on disk - discovery verifies
        // the repository directory, never the .git entry itself - so no .git file is written.
        var root = Path.Combine(Path.GetTempPath(), $"gw-broker-{Guid.NewGuid():N}");
        var worktree = Path.Combine(root, "worktree");
        var plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(plain);
        try
        {
            var configuration = new GitWizardConfiguration();
            configuration.SearchPaths.Add(root);
            var paths = new SortedSet<string>();

            var scanned = new List<ScanRecord>
            {
                new(0, 0, 0, 0, 0, IsDirectory: true, Name: "worktree", Path: worktree),
                new(0, 0, 0, 0, 0, IsDirectory: true, Name: "plain", Path: plain),
                new(0, 0, 0, 0, 0, IsDirectory: false, Name: ".git", Path: Path.Combine(worktree, ".git")),
            };

            var result = await GitWizardApi.TryFindAllRepositoriesUsingMftAsync(
                configuration, paths,
                elevation: new FakeElevationProvider { Elevated = false },
                scanProvider: _ => Task.FromResult<IReadOnlyList<ScanRecord>>(scanned));

            Assert.That(result, Is.True);
            Assert.That(paths, Has.Count.EqualTo(1));
            Assert.That(paths, Has.Some.EndsWith("worktree").IgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Drives a JournalBrokerScanSession over a fake broker client (MFTLibTestExtensions'
    // harness) the same way BrokerScanAsync does, and asserts on the actual ArmAndScan
    // wire frame - the arguments GitWizard's discovery layer commits to, not just the
    // in-process scanProvider seam the tests above exercise.
    [Test]
    [Platform("Win")]
    public async Task BrokerScanAsync_SendsGitDiscoveryProfileAndKeepFileName_OverTheWire()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = new JournalBrokerClient(
            pipe: clientSide,
            mmfReader: new EmptyMmfReader(),
            createDriveMmf: (letter, _) => ($"gw-test-{letter}", NoOpDisposable.Instance));

        var armAndScanFrame = default(BrokerFrame);
        var brokerTask = Task.Run(async () =>
        {
            armAndScanFrame = await ReadOneFrameAsync(serverSide);

            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(0UL, 0L));
            BrokerProtocol.WriteScanReady(response, "gw-test-C", 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(0UL, 0L), []);
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await using var session = await ScanSessionTestHarness.StartScannedAsync(
            _ => Task.FromResult(client), ["C:\\"],
            GitWizardApi.GitDiscoveryScanProfile, [GitWizardApi.GitEntryName]);
        await brokerTask;

        Assert.That(armAndScanFrame.Kind, Is.EqualTo(BrokerFrameKind.ArmAndScan));

        var driveToken = armAndScanFrame.DrivesSpec!.Split(',').Single().Split(':');
        Assert.That(driveToken[0], Is.EqualTo("C"));
        Assert.That(driveToken[^1], Is.EqualTo(((int)GitWizardApi.GitDiscoveryScanProfile).ToString(CultureInfo.InvariantCulture)));
        Assert.That(armAndScanFrame.KeepFileNames, Is.EqualTo(new[] { GitWizardApi.GitEntryName }));
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

    sealed class EmptyMmfReader : IMmfReader
    {
        public ScanRecord[] Read(string mmfName, long byteLength) => [];
    }

    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }

    // In-memory full-duplex stream pair backed by two Pipes, so this test can drive
    // JournalBrokerClient over real async stream IO without a named pipe or elevated broker.
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
}
