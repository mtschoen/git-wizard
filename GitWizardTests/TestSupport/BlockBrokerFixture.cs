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
    public int ShutdownFrames { get; private set; }
    public bool ClientTransportDisposed => _clientStream.IsDisposed;
    public UsnJournalEntry[] WatchEntries { get; set; } = [];
    public List<BlockFileCreateOptions> SectionOptions { get; } = [];
    public List<BrokerFrame> ScanFrames { get; } = [];
    public string? FailedDrive { get; set; }
    public Action? ScanReceived { get; set; }
    public BrokerScanProgress? ScanProgressToEmit { get; set; }
    public Func<Task>? MidScanAsync { get; set; }
    public int RowsWritten { get; private set; }
    public bool ServedToCompletion { get; private set; }
    public bool DisconnectOnScan { get; set; }
    readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Shutdown => _shutdown.Task;
    readonly CancellationTokenSource _clientCancelledSource = new();
    CancellationToken _clientCancellationToken = CancellationToken.None;
    bool _clientCancellationAssigned;

    /// <summary>
    /// The token whose cancellation the fixture treats as the client abandoning its requests. It defaults
    /// to the last cancelable connect token; an assigned token takes precedence over later connects.
    /// </summary>
    public CancellationToken ClientCancellation
    {
        get => _clientCancellationToken;
        set
        {
            _clientCancellationToken = value;
            _clientCancellationAssigned = true;
        }
    }

    bool IsCancelledOrClosed =>
        _cancellation.IsCancellationRequested ||
        _clientCancelledSource.IsCancellationRequested ||
        _clientCancellationToken.IsCancellationRequested ||
        _serverStream.IsPeerDisposed;

    public Task<JournalBrokerClient> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectionCount++;
        if (cancellationToken.CanBeCanceled && !_clientCancellationAssigned)
        {
            _clientCancellationToken = cancellationToken;
        }
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
            while (!IsCancelledOrClosed)
            {
                var frame = await ReadFrameAsync(_serverStream, _cancellation.Token).ConfigureAwait(false);
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
                            await _serverStream.DisposeAsync().ConfigureAwait(false);
                            return;
                        }
                        if (IsCancelledOrClosed)
                        {
                            return;
                        }
                        var scanSucceeded = await WriteScanAsync(response, frame).ConfigureAwait(false);
                        if (!scanSucceeded || IsCancelledOrClosed)
                        {
                            return;
                        }

                        break;
                    case BrokerFrameKind.StartWatch:
                        foreach (var token in frame.DrivesSpec!.Split(','))
                        {
                            var parts = token.Split(':');
                            var armEpoch = uint.Parse(parts[3], CultureInfo.InvariantCulture);
                            BrokerProtocol.WriteJournalBatch(response, parts[0], armEpoch, Cursor, WatchEntries);
                            BrokerProtocol.WriteCaughtUp(response, parts[0], armEpoch);
                        }
                        _watchStarted.TrySetResult(frame);
                        break;
                    case BrokerFrameKind.EndWatch:
                        BrokerProtocol.WriteEndWatchAck(response);
                        break;
                    case BrokerFrameKind.Shutdown:
                        ShutdownFrames++;
                        _shutdown.TrySetResult();
                        return;
                    default:
                        throw new InvalidDataException($"Unexpected client frame {frame.Kind}.");
                }
                if (IsCancelledOrClosed)
                {
                    return;
                }
                try
                {
                    await _serverStream.WriteAsync(response.WrittenMemory, _cancellation.Token).ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (_serverStream.IsPeerDisposed)
                {
                    // PipeWriter rejects writes after the peer completes its reader.
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (IsCancelledOrClosed) { }
        catch (ObjectDisposedException) when (IsCancelledOrClosed) { }
        catch (IOException) when (IsCancelledOrClosed) { }
        finally
        {
            await _serverStream.DisposeAsync().ConfigureAwait(false);
            // Signal every exit; DisconnectAsync still propagates the broker task's failure.
            _shutdown.TrySetResult();
        }
    }

    async Task<bool> WriteScanAsync(ArrayBufferWriter<byte> response, BrokerFrame frame)
    {
        foreach (var token in frame.DrivesSpec!.Split(','))
        {
            if (IsCancelledOrClosed)
            {
                return false;
            }

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
            if (!TryWriteDirectory(writer, 5, "root", 5))
            {
                return false;
            }

            if (MidScanAsync != null)
            {
                await MidScanAsync().ConfigureAwait(false);
            }

            if (IsCancelledOrClosed)
            {
                return false;
            }

            if (!TryWriteDirectory(writer, 6, "code", 5))
            {
                return false;
            }

            if (IsCancelledOrClosed)
            {
                return false;
            }

            if (!TryWriteDirectory(writer, 42, "repo", 6))
            {
                return false;
            }

            if (IsCancelledOrClosed)
            {
                return false;
            }

            if (!TryWriteDirectory(writer, 43, ".git", 42))
            {
                return false;
            }

            if (IsCancelledOrClosed)
            {
                return false;
            }

            try
            {
                writer.SetJournalCursor(Cursor.JournalId, Cursor.NextUsn);
                writer.Complete(PackedIndexFixture.CompletionTimestamp);
            }
            catch (ObjectDisposedException) when (IsCancelledOrClosed)
            {
                return false;
            }

            if (ScanProgressToEmit is { } scanProgress)
            {
                BrokerProtocol.WriteScanProgress(response, scanProgress);
            }

            BrokerProtocol.WriteCursor(response, drive, Cursor);
            BrokerProtocol.WriteScanReady(response, sectionName, block.Header.RowCount, block.Header.NamePoolUsed, 0);
            BrokerProtocol.WriteJournalBatch(response, drive, BrokerFrame.NoArmEpoch, Cursor, _catchUpEntries);
        }

        ServedToCompletion = true;
        return true;
    }

    bool TryWriteDirectory(BlockWriter writer, uint row, string name, uint parent)
    {
        try
        {
            if (IsCancelledOrClosed)
            {
                return false;
            }

            var written = writer.TryWriteRow(row, name, new RowColumns(parent,
                RowFlags.InUse | RowFlags.Directory, (uint)FileAttributes.Directory, 0,
                PackedIndexFixture.CompletionTimestamp.Ticks, 1));
            if (written)
            {
                RowsWritten++;
            }

            return written;
        }
        catch (ObjectDisposedException) when (IsCancelledOrClosed)
        {
            return false;
        }
    }

    static async Task<BrokerFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        var bytes = new byte[4 + length];
        header.CopyTo(bytes.AsMemory());
        await stream.ReadExactlyAsync(bytes.AsMemory(4), cancellationToken).ConfigureAwait(false);
        return BrokerProtocol.ReadFrame(bytes, out _);
    }

    public async Task DisconnectAsync()
    {
        await _cancellation.CancelAsync();
        try
        {
            await _brokerTask;
        }
        finally
        {
            await _serverStream.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_session is not null)
                await _session.DisposeAsync();
            await DisconnectAsync();
        }
        finally
        {
            await _clientStream.DisposeAsync();
            _clientCancelledSource.Dispose();
            _cancellation.Dispose();
            _files.Dispose();
        }
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
