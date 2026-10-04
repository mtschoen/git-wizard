using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GitWizard;
using MFTLib;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

/// <summary>
/// A real <see cref="JournalBrokerHost" /> served in process through <see cref="BrokerTestHarness" />,
/// with fake volume sources: every scan writes the same four directory rows (root, code, repo and
/// its .git marker) into the section the client created, and every watch yields
/// <see cref="WatchEntries" /> once and then waits for its channel to close.
/// </summary>
internal sealed class BlockBrokerFixture : IAsyncDisposable
{
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    static readonly UsnJournalCursor Cursor = new(100UL, 1000L);
    static readonly UsnJournalCursor CaughtUpCursor = new(100UL, 1500L);
    static readonly UsnJournalCursor WatchCursor = new(100UL, 2000L);

    readonly PackedIndexFixture _files = new();
    readonly ConcurrentDictionary<string, BlockFile> _sections = new(StringComparer.Ordinal);
    readonly List<InProcessBrokerHandle> _processes = [];
    InProcessBrokerHandle? _handle;
    readonly UsnJournalEntry[] _catchUpEntries;
    readonly string? _scanError;
    readonly TaskCompletionSource<(string Drive, UsnJournalCursor Since)> _watchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ConcurrentQueue<string> _scannedDrives = new();
    readonly ConcurrentQueue<BlockFileCreateOptions> _sectionOptions = new();
    readonly ConcurrentDictionary<string, bool> _armedDrives = new(StringComparer.OrdinalIgnoreCase);
    int _rowsWritten;
    int _sectionDisposals;
    int _servedToCompletion;

    public BlockBrokerFixture(UsnJournalEntry[]? catchUpEntries = null, string? scanError = null)
    {
        _catchUpEntries = catchUpEntries ?? [];
        _scanError = scanError;
    }

    public string CacheDirectory => _files.BlockDirectory;
    public IndexedDrive Drive(char letter) => new(letter, _files.TreePath, 1);
    public int ConnectionCount { get; private set; }

    /// <summary>Completes when a process this fixture started has ended, for any reason.</summary>
    public Task Shutdown => _shutdown.Task;

    /// <summary>The drive and starting cursor of the first watch the host started.</summary>
    public Task<(string Drive, UsnJournalCursor Since)> WatchStarted => _watchStarted.Task;

    public UsnJournalEntry[] WatchEntries { get; set; } = [];
    public string? FailedDrive { get; set; }
    public Action? ScanReceived { get; set; }
    public BrokerScanProgress? ScanProgressToEmit { get; set; }

    /// <summary>
    /// Runs on the host's scan after the first row is written, with the host's own cancellation
    /// token; the scan waits for the returned task.
    /// </summary>
    public Func<CancellationToken, Task>? MidScanAsync { get; set; }

    /// <summary>Ends the client process from inside the host's scan, as a broker crash would.</summary>
    public bool DisconnectOnScan { get; set; }

    public IReadOnlyList<string> ScannedDrives => _scannedDrives.ToArray();
    public IReadOnlyList<BlockFileCreateOptions> SectionOptions => _sectionOptions.ToArray();
    public MftBlockRowFilter? LastFilter { get; private set; }
    public int RowsWritten => Volatile.Read(ref _rowsWritten);
    public int SectionDisposals => Volatile.Read(ref _sectionDisposals);
    public bool ServedToCompletion => Volatile.Read(ref _servedToCompletion) != 0;

    public Task<BrokerProcess> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectionCount++;
        var host = new JournalBrokerHost(QueryCursor, ScanDrive, ReadJournal, WatchDriveAsync,
            _ => new NtfsVolumeInformation(256 * 1024, 1024));
        var handle = BrokerTestHarness.StartInProcess(host, new SectionWriter(this), CreateSection);
        lock (_processes)
            _processes.Add(handle);
        _handle = handle;
        _ = ObserveShutdownAsync(handle.Process);
        return Task.FromResult(handle.Process);
    }

    async Task ObserveShutdownAsync(BrokerProcess process)
    {
        await process.Ended.ConfigureAwait(false);
        _shutdown.TrySetResult();
    }

    /// <summary>Ends the last process, as the elevated broker exiting would.</summary>
    public async Task DisconnectAsync()
    {
        if (_handle is not { } handle)
            return;
        handle.Crash();
        await handle.Process.Ended.ConfigureAwait(false);
    }

    public static BrokerScanOptions CreateOptions() => new()
    {
        Profile = GitWizardApi.GitDiscoveryScanProfile,
        KeepFileNames = [GitWizardApi.GitEntryName]
    };

    public BlockScanTarget CreateTarget(char drive) =>
        new(Path.Combine(_files.BlockDirectory, $"{drive}-{Guid.NewGuid():N}.mlix"), 1, DeleteOnClose: true);

    (string SectionName, BlockFile Block, IDisposable Lifetime) CreateSection(char drive, BlockFileCreateOptions options)
    {
        _sectionOptions.Enqueue(options);
        var name = $"gw-test-{drive}-{Guid.NewGuid():N}";
        var block = BlockFile.Create(options);
        _sections[name] = block;
        return (name, block, new SectionLifetime(() => Interlocked.Increment(ref _sectionDisposals)));
    }

    IEnumerable<IReadOnlyList<MftRecord>> ScanDrive(string drive, ParseThreadAllowance parseThreads,
        IBrokerOperationReporter operation, IProgress<BlockWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        _scannedDrives.Enqueue(drive);
        ScanReceived?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (DisconnectOnScan)
        {
            // The scan fails only once the client has seen its process end, so the failure the
            // client observes is the crash, never an ordinary scan error.
            _handle!.Crash();
            Shutdown.Wait(HangGuard, CancellationToken.None);
            throw new IOException("The broker host crashed during the scan.");
        }
        if (_scanError != null && (FailedDrive == null || FailedDrive == drive))
            throw new IOException(_scanError);
        if (ScanProgressToEmit is { } sample)
        {
            progress?.Report(new BlockWriteProgress(sample.RecordsProcessed, sample.BytesProcessed,
                sample.TotalRecords, sample.TotalBytes, sample.Phase));
        }
        return [];
    }

    // The journal's tip: the first query of a drive arms its scan at Cursor, and the catch-up
    // entries, when any, are then journaled between Cursor and CaughtUpCursor.
    UsnJournalCursor QueryCursor(string drive) =>
        _armedDrives.TryAdd(drive, true) || _catchUpEntries.Length == 0 ? Cursor : CaughtUpCursor;

    (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(string drive, UsnJournalCursor since,
        int maximumBufferReads) =>
        since == Cursor && _catchUpEntries.Length > 0 ? (_catchUpEntries, CaughtUpCursor) : ([], since);

    async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchDriveAsync(string drive,
        UsnJournalCursor since, IBrokerOperationReporter operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _watchStarted.TrySetResult((drive, since));
        // A watch resumed from before the catch-up entries replays them, as the journal would.
        if (since == Cursor && _catchUpEntries.Length > 0)
            yield return (_catchUpEntries, CaughtUpCursor);
        if (WatchEntries.Length > 0)
            yield return (WatchEntries, WatchCursor);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    void WriteRows(BlockFile block, UsnJournalCursor cursor, CancellationToken cancellationToken)
    {
        var writer = new BlockWriter(block);
        WriteDirectory(writer, 5, "root", 5);
        if (MidScanAsync is { } midScan)
            midScan(cancellationToken).Wait(HangGuard, CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        WriteDirectory(writer, 6, "code", 5);
        WriteDirectory(writer, 42, "repo", 6);
        WriteDirectory(writer, 43, ".git", 42);
        cancellationToken.ThrowIfCancellationRequested();
        writer.SetJournalCursor(cursor.JournalId, cursor.NextUsn);
        writer.Complete(PackedIndexFixture.CompletionTimestamp, null);
        Volatile.Write(ref _servedToCompletion, 1);
    }

    void WriteDirectory(BlockWriter writer, uint row, string name, uint parent)
    {
        if (writer.TryWriteRow(row, name, new RowColumns(parent, RowFlags.InUse | RowFlags.Directory,
                (uint)FileAttributes.Directory, 0, PackedIndexFixture.CompletionTimestamp.Ticks, 1)))
        {
            Interlocked.Increment(ref _rowsWritten);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            InProcessBrokerHandle[] processes;
            lock (_processes)
                processes = _processes.ToArray();
            foreach (var handle in processes)
                await handle.DisposeAsync();
        }
        finally
        {
            foreach (var block in _sections.Values)
                block.Dispose();
            _files.Dispose();
        }
    }

    sealed class SectionWriter(BlockBrokerFixture fixture) : IBlockSectionWriter
    {
        public BlockWriteResult Write(string sectionName, UsnJournalCursor cursor,
            IEnumerable<IReadOnlyList<MftRecord>> batches, MftBlockRowFilter filter, BlockWriteReporting reporting,
            CancellationToken cancellationToken)
        {
            fixture.LastFilter = filter;
            // The fake scan yields no records; enumerating it runs the scan source's own checks.
            if (batches.Any())
                throw new InvalidOperationException("The fixture's scan source yields no records.");
            var block = fixture._sections[sectionName];
            fixture.WriteRows(block, cursor, cancellationToken);
            return new BlockWriteResult(block.Header.RowCount, block.Header.NamePoolUsed, 0, false);
        }
    }

    sealed class SectionLifetime(Action onDispose) : IDisposable
    {
        int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
        }
    }
}
