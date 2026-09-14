using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using GitWizard;
using MFTLib;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

internal sealed class BlockBrokerFixture : IAsyncDisposable
{
    readonly PackedIndexFixture _files = new();
    readonly Dictionary<string, BlockFile> _sections = new(StringComparer.Ordinal);
    readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(15));
    readonly DuplexStream _clientStream;
    readonly DuplexStream _serverStream;
    readonly Task _brokerTask;
    readonly UsnJournalEntry[] _catchUpEntries;
    readonly string? _scanError;
    readonly TaskCompletionSource<BrokerFrame> _watchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    JournalBrokerScanSession? _session;
    public string CacheDirectory => _files.BlockDirectory;
    public IndexedDrive Drive(char letter) => new(letter, _files.TreePath, 1);
    public int ConnectionCount { get; private set; }
    public UsnJournalEntry[] WatchEntries { get; set; } = [];
    public List<BlockFileCreateOptions> SectionOptions { get; } = [];
    public List<BrokerFrame> ScanFrames { get; } = [];
    public string? FailedDrive { get; set; }
    public Action? ScanReceived { get; set; }
    public bool DisconnectOnScan { get; set; }
    readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Shutdown => _shutdown.Task;

    public Task<JournalBrokerClient> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectionCount++;
        return Task.FromResult(new JournalBrokerClient(_clientStream, CreateSection));
    }

    public BrokerFrame ScanFrame { get; private set; }
    public int SectionDisposals { get; private set; }
    public Task<BrokerFrame> WatchStarted => _watchStarted.Task;
    public JournalBrokerScanSession Session => _session ??
        throw new InvalidOperationException("The broker fixture has not started.");
    public IReadOnlyDictionary<string, BlockScanOutcome> Blocks => Session.LatestScan!.BlockOutcomes;
    static readonly UsnJournalCursor Cursor = new(100UL, 1000L);

    public BlockBrokerFixture(UsnJournalEntry[]? catchUpEntries = null, string? scanError = null)
    {
        _catchUpEntries = catchUpEntries ?? [];
        _scanError = scanError;
        (_clientStream, _serverStream) = DuplexStream.CreatePair();
        _brokerTask = ServeAsync();
    }

    public BrokerScanOptions CreateOptions(IReadOnlyList<string> drives)
    {
        var targets = new Dictionary<string, BlockScanTarget>();
        foreach (var drive in drives)
        {
            var identity = new IndexedDrive(char.ToUpperInvariant(drive[0]), _files.TreePath, 1);
            targets.Add(drive, new BlockScanTarget(
                Path.Combine(_files.BlockDirectory, $"{identity.DriveLetter}-{Guid.NewGuid():N}.mlix"),
                identity.VolumeSerial, DeleteOnClose: true));
        }
        return new BrokerScanOptions
        {
            Profile = GitWizardApi.GitDiscoveryScanProfile,
            KeepFileNames = [GitWizardApi.GitEntryName],
            BlockTargets = targets
        };
    }

    public async Task StartAsync(IReadOnlyList<string>? drives = null)
    {
        drives ??= ["C"];
        var client = new JournalBrokerClient(_clientStream, CreateSection);
        _session = await ScanSessionTestHarness.StartScannedAsync(
            _ => Task.FromResult(client), drives, CreateOptions(drives), _cancellation.Token);
    }

    (string SectionName, BlockFile Block, IDisposable Lifetime) CreateSection(string drive, BlockFileCreateOptions options)
    {
        SectionOptions.Add(options);
        var name = "gw-test-" + drive;
        var block = BlockFile.Create(options);
        _sections.Add(name, block);
        return (name, block, new SectionLifetime(() => SectionDisposals++));
    }

    async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                var frame = await ReadFrameAsync(_serverStream, _cancellation.Token);
                var response = new ArrayBufferWriter<byte>();
                switch (frame.Kind)
                {
                    case BrokerFrameKind.QueryVolumes:
                        foreach (var token in frame.DrivesSpec!.Split(','))
                            BrokerProtocol.WriteVolumeInfo(response, token.Split(':')[0], 256, 1024, 256 * 1024);
                        break;
                    case BrokerFrameKind.ArmAndScan:
                        ScanFrame = frame;
                        ScanFrames.Add(frame);
                        ScanReceived?.Invoke();
                        if (DisconnectOnScan)
                        {
                            await _serverStream.DisposeAsync();
                            return;
                        }
                        try
                        {
                            WriteScan(response, frame);
                        }
                        catch (ObjectDisposedException)
                        {
                            // The client cancelled mid-scan and released its block section, so the
                            // rows this scan was writing have nowhere to land. A real broker sees
                            // the same thing when its client vanishes: end the session rather than
                            // faulting the server task, which would surface later as an unrelated
                            // failure from DisconnectAsync. Narrow to the scan write on purpose -
                            // a disposed object anywhere else is still a defect.
                            return;
                        }

                        break;
                    case BrokerFrameKind.StartWatch:
                        foreach (var token in frame.DrivesSpec!.Split(','))
                        {
                            var parts = token.Split(':');
                            BrokerProtocol.WriteJournalBatch(response, parts[0],
                                uint.Parse(parts[3], CultureInfo.InvariantCulture), Cursor, WatchEntries);
                        }
                        _watchStarted.TrySetResult(frame);
                        break;
                    case BrokerFrameKind.EndWatch:
                        BrokerProtocol.WriteEndWatchAck(response);
                        break;
                    case BrokerFrameKind.Shutdown:
                        _shutdown.TrySetResult();
                        return;
                    default:
                        throw new InvalidDataException($"Unexpected client frame {frame.Kind}.");
                }
                await _serverStream.WriteAsync(response.WrittenMemory, _cancellation.Token);
            }
        }
        catch (EndOfStreamException) { }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        finally
        {
            // Shutdown means "the server stopped serving", not "the client sent a Shutdown frame".
            // It used to be set only by that frame, so every other exit - a cancelled client that
            // never says goodbye, an ended stream, a fault - left anything awaiting Shutdown to
            // time out instead of failing for its real reason. TrySetResult keeps the explicit
            // frame path a no-op when it already fired.
            _shutdown.TrySetResult();
        }
    }

    void WriteScan(ArrayBufferWriter<byte> response, BrokerFrame frame)
    {
        foreach (var token in frame.DrivesSpec!.Split(','))
        {
            var parts = token.Split(':');
            var drive = parts[0];
            if (_scanError != null && (FailedDrive == null || FailedDrive == drive))
            {
                BrokerProtocol.WriteError(response, drive, BrokerFrame.NoArmEpoch, _scanError);
                continue;
            }
            var sectionName = parts[3];
            var block = _sections[sectionName];
            var writer = new BlockWriter(block);
            WriteDirectory(writer, 5, "root", 5);
            WriteDirectory(writer, 6, "code", 5);
            WriteDirectory(writer, 42, "repo", 6);
            WriteDirectory(writer, 43, ".git", 42);
            writer.SetJournalCursor(Cursor.JournalId, Cursor.NextUsn);
            writer.Complete(PackedIndexFixture.CompletionTimestamp);
            BrokerProtocol.WriteCursor(response, drive, Cursor);
            BrokerProtocol.WriteScanReady(response, sectionName, block.Header.RowCount, block.Header.NamePoolUsed, 0);
            BrokerProtocol.WriteJournalBatch(response, drive, BrokerFrame.NoArmEpoch, Cursor, _catchUpEntries);
        }
    }

    static void WriteDirectory(BlockWriter writer, uint row, string name, uint parent)
    {
        Assert.That(writer.TryWriteRow(row, name, new RowColumns(parent,
            RowFlags.InUse | RowFlags.Directory, (uint)FileAttributes.Directory, 0,
            PackedIndexFixture.CompletionTimestamp.Ticks, 1)), Is.True);
    }

    static async Task<BrokerFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        var bytes = new byte[4 + length];
        header.CopyTo(bytes.AsMemory());
        await stream.ReadExactlyAsync(bytes.AsMemory(4), cancellationToken);
        return BrokerProtocol.ReadFrame(bytes, out _);
    }

    public async Task DisconnectAsync()
    {
        await _cancellation.CancelAsync();
        await _brokerTask;
        await _serverStream.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
            await _session.DisposeAsync();
        await DisconnectAsync();
        await _clientStream.DisposeAsync();
        _cancellation.Dispose();
        _files.Dispose();
    }

    sealed class SectionLifetime(Action onDispose) : IDisposable
    {
        bool _disposed;
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            onDispose();
        }
    }
}
