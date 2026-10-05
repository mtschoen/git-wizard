using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MFTLib;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

/// <summary>
/// A real broker host served in process through <see cref="BrokerTestHarness" />, with fake volumes:
/// every scan yields the same four directory records (root, code, repo and its .git marker), and
/// every watch yields <see cref="WatchEntries" /> once and then waits for its channel to close.
/// </summary>
internal sealed class BlockBrokerFixture : IAsyncDisposable
{
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    static readonly UsnJournalCursor Cursor = new(100UL, 1000L);
    static readonly UsnJournalCursor CaughtUpCursor = new(100UL, 1500L);
    static readonly UsnJournalCursor WatchCursor = new(100UL, 2000L);

    readonly PackedIndexFixture _files = new();
    readonly List<InProcessBrokerHandle> _processes = [];
    readonly ScriptedBrokerVolumes _volumes;
    InProcessBrokerHandle? _handle;
    readonly UsnJournalEntry[] _catchUpEntries;
    readonly string? _scanError;
    readonly TaskCompletionSource<(string Drive, UsnJournalCursor Since)> _watchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ConcurrentDictionary<string, bool> _armedDrives = new(StringComparer.OrdinalIgnoreCase);
    int _recordsYielded;
    int _scanCompleted;

    public BlockBrokerFixture(UsnJournalEntry[]? catchUpEntries = null, string? scanError = null)
    {
        _catchUpEntries = catchUpEntries ?? [];
        _scanError = scanError;
        _volumes = new ScriptedBrokerVolumes
        {
            QueryJournalCursor = QueryCursor,
            ScanDrive = ScanDrive,
            ReadJournal = ReadJournal,
            WatchDrive = WatchDriveAsync
        };
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

    /// <summary>The parse progress every scan reports before it yields records, as (records parsed, expected total).</summary>
    public (long RecordsParsed, long? TotalRecords)? ParseProgressToReport { get; set; }

    /// <summary>
    /// Runs on the host's scan right after the parse progress is reported and before any record is
    /// yielded; the scan waits for the returned task. The host keeps only the newest unsent progress
    /// sample, so a test that needs the parse sample relayed holds the scan here, before the block
    /// writer's own progress can replace it.
    /// </summary>
    public Func<CancellationToken, Task>? AfterParseProgressAsync { get; set; }

    /// <summary>
    /// Runs on the host's scan after the first record is yielded, with the host's own cancellation
    /// token; the scan waits for the returned task.
    /// </summary>
    public Func<CancellationToken, Task>? MidScanAsync { get; set; }

    /// <summary>Ends the client process from inside the host's scan, as a broker crash would.</summary>
    public bool DisconnectOnScan { get; set; }

    /// <summary>Every scan any process of this fixture served, in order, as the client requested it.</summary>
    public IReadOnlyList<InProcessBrokerScan> Scans
    {
        get
        {
            lock (_processes)
                return _processes.SelectMany(handle => handle.Scans).ToArray();
        }
    }

    public IReadOnlyList<string> ScannedDrives => Scans.Select(scan => scan.DriveLetter).ToArray();

    /// <summary>How many records the scans handed to the block writer so far.</summary>
    public int RecordsYielded => Volatile.Read(ref _recordsYielded);

    /// <summary>True once a scan yielded every record and its enumeration ran to the end.</summary>
    public bool ScanCompleted => Volatile.Read(ref _scanCompleted) != 0;

    public Task<BrokerProcess> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectionCount++;
        var handle = BrokerTestHarness.StartInProcess(_volumes);
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

    IEnumerable<IReadOnlyList<MftRecord>> ScanDrive(ScriptedScan scan)
    {
        ScanReceived?.Invoke();
        scan.CancellationToken.ThrowIfCancellationRequested();
        if (DisconnectOnScan)
        {
            // The scan fails only once the client has seen its process end, so the failure the
            // client observes is the crash, never an ordinary scan error.
            _handle!.Crash();
            Shutdown.Wait(HangGuard, CancellationToken.None);
            throw new IOException("The broker host crashed during the scan.");
        }
        if (_scanError != null && (FailedDrive == null || FailedDrive == scan.DriveLetter))
            throw new IOException(_scanError);
        if (ParseProgressToReport is { } sample)
            scan.ReportParsed(sample.RecordsParsed, sample.TotalRecords);
        AfterParseProgressAsync?.Invoke(scan.CancellationToken).Wait(HangGuard, CancellationToken.None);
        return YieldRecords(scan.CancellationToken);
    }

    IEnumerable<IReadOnlyList<MftRecord>> YieldRecords(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _recordsYielded);
        yield return [Directory(5, ".", 5)];
        if (MidScanAsync is { } midScan)
            midScan(cancellationToken).Wait(HangGuard, CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Add(ref _recordsYielded, 3);
        yield return [Directory(6, "code", 5), Directory(42, "repo", 6), Directory(43, ".git", 42)];
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _scanCompleted, 1);
    }

    static MftRecord Directory(ulong recordNumber, string name, ulong parentRecordNumber) =>
        SyntheticMftRecord.Create(new SyntheticMftRecordOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentRecordNumber,
            FileName = name,
            IsDirectory = true
        });

    // The journal's tip: the first query of a drive arms its scan at Cursor, and the catch-up
    // entries, when any, are then journaled between Cursor and CaughtUpCursor.
    UsnJournalCursor QueryCursor(string drive) =>
        _armedDrives.TryAdd(drive, true) || _catchUpEntries.Length == 0 ? Cursor : CaughtUpCursor;

    (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(string drive, UsnJournalCursor since,
        int maximumBufferReads) =>
        since == Cursor && _catchUpEntries.Length > 0 ? (_catchUpEntries, CaughtUpCursor) : ([], since);

    async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchDriveAsync(string drive,
        UsnJournalCursor since, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _watchStarted.TrySetResult((drive, since));
        // A watch resumed from before the catch-up entries replays them, as the journal would.
        if (since == Cursor && _catchUpEntries.Length > 0)
            yield return (_catchUpEntries, CaughtUpCursor);
        if (WatchEntries.Length > 0)
            yield return (WatchEntries, WatchCursor);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
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
            _files.Dispose();
        }
    }
}
