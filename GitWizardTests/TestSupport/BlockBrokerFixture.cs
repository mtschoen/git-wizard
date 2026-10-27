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
sealed class BlockBrokerFixture : IAsyncDisposable
{
    static readonly SyntheticJournalCursor Cursor = new(100UL, 1000L);
    static readonly SyntheticJournalCursor CaughtUpCursor = new(100UL, 1500L);
    static readonly SyntheticJournalCursor WatchCursor = new(100UL, 2000L);

    readonly PackedIndexFixture _files = new();
    readonly List<InProcessBrokerHandle> _processes = [];
    readonly ScriptedBrokerVolumes _volumes;
    InProcessBrokerHandle? _handle;
    readonly SyntheticJournalRecord[] _catchUpEntries;
    readonly string? _scanError;
    readonly TaskCompletionSource<(string Drive, SyntheticJournalCursor Since)> _watchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ConcurrentDictionary<string, bool> _armedDrives = new(StringComparer.OrdinalIgnoreCase);
    int _recordsYielded;
    int _scanCompleted;

    public BlockBrokerFixture(SyntheticJournalRecord[]? catchUpEntries = null, string? scanError = null)
    {
        _catchUpEntries = catchUpEntries ?? [];
        _scanError = scanError;
        _volumes = new ScriptedBrokerVolumes
        {
            QueryJournalCursor = QueryCursor,
            ScanVolume = ScanVolume,
            ReadJournal = ReadJournal,
            WatchVolume = WatchDriveAsync,
            GrowUsnJournal = GrowJournal
        };
    }

    public string CacheDirectory => _files.BlockDirectory;
    public IndexedRoot Drive(char letter) => SyntheticIndexedRoot.Create(new VolumeKey(letter), _files.TreePath, 1);
    public int ConnectionCount { get; private set; }

    /// <summary>Answers the host's grow requests; unset refuses the grow, as a host without a source does.</summary>
    public Func<string, long, long, UsnJournalSettings>? GrowUsnJournal { get; set; }

    /// <summary>A broker session whose launch starts an in-process broker of this fixture.</summary>
    public BrokerSession CreateSession() => BrokerTestHarness.CreateSession(ConnectAsync);

    /// <summary>Completes when a process this fixture started has ended, for any reason.</summary>
    public Task Shutdown => _shutdown.Task;

    /// <summary>The drive and starting cursor of the first watch the host started.</summary>
    public Task<(string Drive, SyntheticJournalCursor Since)> WatchStarted => _watchStarted.Task;

    public SyntheticJournalRecord[] WatchEntries { get; set; } = [];
    public SyntheticScanRecord[] AdditionalScanRecords { get; set; } = [];
    public string? FailedDrive { get; init; }
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
    public bool DisconnectOnScan { get; init; }

    /// <summary>Every scan any process of this fixture served, in order, as the client requested it.</summary>
    public IReadOnlyList<InProcessBrokerScan> Scans
    {
        get
        {
            lock (_processes)
            {
                return (InProcessBrokerScan[])[.. _processes.SelectMany(handle => handle.Scans)];
            }
        }
    }

    public IReadOnlyList<string> ScannedDrives => (string[])[.. Scans.Select(scan => scan.Volume.ToString())];

    /// <summary>How many records the scans handed to the block writer so far.</summary>
    public int RecordsYielded => Volatile.Read(ref _recordsYielded);

    /// <summary>True once a scan yielded every record and its enumeration ran to the end.</summary>
    public bool ScanCompleted => Volatile.Read(ref _scanCompleted) != 0;

    public Task<InProcessBrokerHandle> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectionCount++;
        var handle = BrokerTestHarness.StartInProcess(_volumes);
        lock (_processes)
        {
            _processes.Add(handle);
        }

        _handle = handle;
        _ = ObserveShutdownAsync(handle);
        return Task.FromResult(handle);
    }

    async Task ObserveShutdownAsync(InProcessBrokerHandle handle)
    {
        await handle.Ended.ConfigureAwait(false);
        _shutdown.TrySetResult();
    }

    /// <summary>Ends the last process, as the elevated broker exiting would.</summary>
    public async Task DisconnectAsync()
    {
        if (_handle is not { } handle)
        {
            return;
        }

        handle.Crash();
        await handle.Ended.ConfigureAwait(false);
    }

    IEnumerable<IReadOnlyList<SyntheticScanRecord>> ScanVolume(ScriptedScan scan)
    {
        ScanReceived?.Invoke();
        scan.CancellationToken.ThrowIfCancellationRequested();
        if (DisconnectOnScan)
        {
            // The scan fails only once the client has seen its process end, so the failure the
            // client observes is the crash, never an ordinary scan error.
            _handle!.Crash();
            Shutdown.Wait(TestTimeouts.BrokerHangGuard, CancellationToken.None);
            throw new IOException("The broker host crashed during the scan.");
        }
        if (_scanError != null && (FailedDrive == null || FailedDrive == scan.Volume.ToString()))
        {
            throw new IOException(_scanError);
        }

        if (ParseProgressToReport is { } sample)
        {
            scan.ReportParsed(sample.RecordsParsed, sample.TotalRecords);
        }

        AfterParseProgressAsync?.Invoke(scan.CancellationToken).Wait(TestTimeouts.BrokerHangGuard, CancellationToken.None);
        return YieldRecords(scan.CancellationToken);
    }

    IEnumerable<IReadOnlyList<SyntheticScanRecord>> YieldRecords(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _recordsYielded);
        yield return [Directory(5, ".", 5)];
        if (MidScanAsync is { } midScan)
        {
            midScan(cancellationToken).Wait(TestTimeouts.BrokerHangGuard, CancellationToken.None);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Add(ref _recordsYielded, 3);
        yield return [Directory(6, "code", 5), Directory(42, "repo", 6), Directory(43, ".git", 42)];
        if (AdditionalScanRecords.Length > 0)
        {
            Interlocked.Add(ref _recordsYielded, AdditionalScanRecords.Length);
            yield return AdditionalScanRecords;
        }
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _scanCompleted, 1);
    }

    static SyntheticScanRecord Directory(ulong recordNumber, string name, ulong parentRecordNumber) =>
        new()
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentRecordNumber,
            FileName = name,
            IsDirectory = true
        };

    // The journal's tip: the first query of a drive arms its scan at Cursor, and the catch-up
    // entries, when any, are then journaled between Cursor and CaughtUpCursor.
    SyntheticJournalCursor QueryCursor(VolumeKey drive) =>
        _armedDrives.TryAdd(drive.ToString(), true) || _catchUpEntries.Length == 0 ? Cursor : CaughtUpCursor;

    (IReadOnlyList<SyntheticJournalRecord> Entries, SyntheticJournalCursor Updated) ReadJournal(VolumeKey drive, SyntheticJournalCursor since,
        int maximumBufferReads) =>
        since == Cursor && _catchUpEntries.Length > 0 ? (_catchUpEntries, CaughtUpCursor) : ([], since);

    async IAsyncEnumerable<(IReadOnlyList<SyntheticJournalRecord> Entries, SyntheticJournalCursor Cursor)> WatchDriveAsync(VolumeKey drive,
        SyntheticJournalCursor since, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _watchStarted.TrySetResult((drive.ToString(), since));
        // A watch resumed from before the catch-up entries replays them, as the journal would.
        if (since == Cursor && _catchUpEntries.Length > 0)
        {
            yield return (_catchUpEntries, CaughtUpCursor);
        }

        if (WatchEntries.Length > 0)
        {
            yield return (WatchEntries, WatchCursor);
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    UsnJournalSettings GrowJournal(VolumeKey drive, UsnJournalSettings target) =>
        GrowUsnJournal?.Invoke(drive.ToString(), target.MaximumSize, target.AllocationDelta)
        ?? throw new InvalidOperationException("The fixture has no grow handler.");

    public async ValueTask DisposeAsync()
    {
        try
        {
            InProcessBrokerHandle[] processes;
            lock (_processes)
            {
                processes = [.. _processes];
            }

            foreach (var handle in processes)
            {
                await handle.DisposeAsync();
            }
        }
        finally
        {
            _files.Dispose();
        }
    }
}
