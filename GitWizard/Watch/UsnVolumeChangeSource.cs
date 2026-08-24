using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using MFTLib;

namespace GitWizard.Watch;

/// <summary>
/// Windows <see cref="IVolumeChangeSource"/> over MFTLib's elevated USN journal broker, driven
/// through a <see cref="JournalBrokerScanSession"/>. The session owns the elevated client, the
/// per-drive resume cursors, and broker-death faulting, so this adapter holds no client and
/// threads no cursors: it arms by starting the session (which spawns, connects, and cold-scans
/// in one call) and watches by asking the session for each drive's live batch enumerable.
/// <para>
/// Live <see cref="UsnJournalEntry"/> batches only carry record numbers and a bare file name,
/// not a full path, so every live entry is resolved back to a full path through a per-drive
/// record-number index seeded from the cold scan: the parent record's indexed path plus the
/// entry's file name, falling back to the entry's own indexed path when its parent was never
/// scanned. Each resolution is folded back into the index so a descendant created later in the
/// same watch (e.g. a ".git" folder inside a directory created moments earlier) also resolves.
/// </para>
/// <para>
/// The cold scan and the start of the live watch are not simultaneous: changes that land in
/// that gap are returned separately by the broker as <see cref="BrokerScanResult.CatchUpEntries"/>.
/// <see cref="WatchAsync"/> replays each drive's catch-up entries as that drive's first batch,
/// through the same resolution/index path as live entries, before any live batch - so the
/// index is exactly as if the catch-up entries were the earliest live batch, and no change in
/// the arm-to-watch-start window is lost.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UsnVolumeChangeSource : IVolumeChangeSource
{
    readonly Func<string, bool> _brokerLaunch;
    readonly Dictionary<string, Dictionary<ulong, string>> _pathIndexByDrive =
        new(StringComparer.OrdinalIgnoreCase);

    JournalBrokerScanSession? _preparedSession;
    IReadOnlyList<ScanRecord>? _preparedRecords;
    IReadOnlyList<string>? _preparedDrives;

    JournalBrokerScanSession? _session;
    IReadOnlyCollection<string> _watchedDrives = [];
    IReadOnlyDictionary<string, UsnJournalEntry[]> _catchUpEntriesByDrive =
        new Dictionary<string, UsnJournalEntry[]>(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? SourceDied;

    public UsnVolumeChangeSource(Func<string, bool> brokerLaunch)
    {
        _brokerLaunch = brokerLaunch;
    }

    public UsnVolumeChangeSource(
        JournalBrokerScanSession preparedSession,
        IReadOnlyList<ScanRecord> scanRecords,
        Func<string, bool> brokerLaunch,
        IReadOnlyList<string>? preparedDrives = null)
    {
        _brokerLaunch = brokerLaunch;
        _preparedSession = preparedSession;
        _preparedRecords = scanRecords;
        _preparedDrives = preparedDrives ?? preparedSession.LatestScan?.AdvancedCursors.Keys.ToList();
    }

    /// <summary>
    /// Starts the session - one elevated broker (single UAC prompt), armed and cold-scanned
    /// across <paramref name="volumes"/> - and seeds the per-drive path index from the
    /// resulting scan records. Reuses any prepared discovery session if its drives match
    /// and it remains operable; otherwise falls back to starting a fresh session. Throws
    /// <see cref="InvalidOperationException"/> if the broker declines to launch or dies before
    /// the scan completes; the session disposes itself on that path, so a failed arm leaves
    /// nothing to clean up.
    /// </summary>
    public async Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        if (GetPreparedSession(volumes) is { } prepared)
        {
            var adoptedSession = prepared.Session;
            _session = adoptedSession;
            _preparedSession = null;
            _preparedRecords = null;
            _preparedDrives = null;

            adoptedSession.Faulted += reason => SourceDied?.Invoke(reason);

            var scan = adoptedSession.LatestScan
                ?? throw new InvalidOperationException("Scanned session unexpectedly has no LatestScan result.");

            _watchedDrives = scan.AdvancedCursors.Keys.ToList();
            _catchUpEntriesByDrive = scan.CatchUpEntries;

            if (prepared.Records != null)
            {
                foreach (var record in prepared.Records)
                    IndexRecord(record.Path, record.RecordNumber);
            }

            foreach (var drive in _watchedDrives)
                GetOrAddIndex(drive);

            return new VolumeArmResult(scan.Errors);
        }

        if (_preparedSession is not null)
        {
            await _preparedSession.DisposeAsync().ConfigureAwait(false);
            _preparedSession = null;
            _preparedRecords = null;
            _preparedDrives = null;
        }

        // Same profile and keep-file name as GitWizardApi's discovery scan: directories (for
        // path resolution) plus .git markers (for repository-root mapping), not the whole MFT.
        var session = await JournalBrokerScanSession.StartAsync(
                _brokerLaunch, volumes.ToList(), GitWizardApi.GitDiscoveryScanProfile,
                (batch, _) =>
                {
                    foreach (var record in batch)
                        IndexRecord(record.Path, record.RecordNumber);
                    return ValueTask.CompletedTask;
                },
                keepFileNames: [GitWizardApi.GitEntryName], ct)
            .ConfigureAwait(false);
        _session = session;

        // Attached after StartAsync rather than before because the session is what raises this:
        // Faulted replays to a handler added after the broker already died, so a death in the
        // arm-to-subscribe window cannot be missed. A death *during* the cold scan never
        // reaches here at all - StartAsync throws instead.
        session.Faulted += reason => SourceDied?.Invoke(reason);

        var freshScan = session.LatestScan
            ?? throw new InvalidOperationException("Scanned session unexpectedly has no LatestScan result.");

        // The session's watch cursor set: exactly the drives that armed successfully, and
        // exactly the keys WatchDriveAsync accepts.
        _watchedDrives = freshScan.AdvancedCursors.Keys.ToList();
        _catchUpEntriesByDrive = freshScan.CatchUpEntries;

        foreach (var drive in _watchedDrives)
            GetOrAddIndex(drive);

        return new VolumeArmResult(freshScan.Errors);
    }

    (JournalBrokerScanSession Session, IReadOnlyList<ScanRecord>? Records)? GetPreparedSession(
        IReadOnlyCollection<string> requestedVolumes)
    {
        if (_preparedSession is null || _preparedSession.IsFaulted || _preparedSession.State != JournalBrokerSessionState.Parked)
            return null;

        var sessionDrives = _preparedDrives ?? _preparedSession.LatestScan?.AdvancedCursors.Keys ?? [];
        var normalizedRequested = requestedVolumes
            .Select(NormalizeDriveToken)
            .Where(drive => !string.IsNullOrEmpty(drive))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedSession = sessionDrives
            .Select(NormalizeDriveToken)
            .Where(drive => !string.IsNullOrEmpty(drive))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!normalizedRequested.SetEquals(normalizedSession))
            return null;

        return (_preparedSession, _preparedRecords);
    }

    static string NormalizeDriveToken(string volume)
    {
        var letter = GitWizardApi.GetDriveLetter(volume);
        if (!string.IsNullOrEmpty(letter))
            return letter;

        var trimmed = volume.TrimEnd(':', '\\', '/');
        return trimmed.Length == 1 ? trimmed.ToUpperInvariant() : volume;
    }

    /// <summary>
    /// Starts the live watch on every drive armed by <see cref="ArmAndCatchUpAsync"/> and
    /// streams one <see cref="VolumeChangeBatch"/> per drive batch, in arrival order across
    /// drives (each drive is pumped concurrently into a shared channel).
    /// </summary>
    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var session = _session
            ?? throw new InvalidOperationException("ArmAndCatchUpAsync must be called before WatchAsync.");

        await session.StartWatchAsync(ct).ConfigureAwait(false);
        var channel = Channel.CreateUnbounded<VolumeChangeBatch>();

        var pumpTask = PumpAllDrivesAsync(session, channel.Writer, ct);
        await foreach (var batch in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return batch;

        await pumpTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_preparedSession is not null)
        {
            await _preparedSession.DisposeAsync().ConfigureAwait(false);
            _preparedSession = null;
            _preparedRecords = null;
            _preparedDrives = null;
        }

        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }

    async Task PumpAllDrivesAsync(
        JournalBrokerScanSession session, ChannelWriter<VolumeChangeBatch> writer, CancellationToken ct)
    {
        try
        {
            var pumps = _watchedDrives.Select(drive => PumpDriveAsync(session, drive, writer, ct));
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    async Task PumpDriveAsync(
        JournalBrokerScanSession session, string drive,
        ChannelWriter<VolumeChangeBatch> writer, CancellationToken ct)
    {
        try
        {
            await EmitCatchUpBatchAsync(drive, writer, ct).ConfigureAwait(false);

            // The session resumes this drive from its own advanced cursor, so none is passed in.
            await foreach (var (entries, _) in session.WatchDriveAsync(drive, ct).ConfigureAwait(false))
            {
                var mapped = MapEntries(drive, entries);
                if (mapped.Count > 0)
                    await writer.WriteAsync(new VolumeChangeBatch(drive, mapped), ct).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException exception) when (!ct.IsCancellationRequested)
        {
            // Per-drive mid-watch fault (e.g. journal invalidated on this volume). MFTLib
            // faults only this drive's enumerable with InvalidOperationException without
            // raising session.Faulted, so signal source death here so consumers handle it
            // cleanly rather than letting the IOE escape.
            SourceDied?.Invoke($"Drive {drive} journal invalidated: {exception.Message}");
        }
    }

    // Replays this drive's catch-up entries - changes that landed between the armed cursor and
    // the advanced cursor, i.e. during the cold scan - as its first batch, through the same
    // resolution/index path as live entries. Must run before the live pump below so the index
    // self-heals from catch-up entries exactly as it would from an equivalent live batch.
    async Task EmitCatchUpBatchAsync(string drive, ChannelWriter<VolumeChangeBatch> writer, CancellationToken ct)
    {
        if (!_catchUpEntriesByDrive.TryGetValue(drive, out var catchUpEntries) || catchUpEntries.Length == 0)
            return;

        var mapped = MapEntries(drive, catchUpEntries);
        if (mapped.Count > 0)
            await writer.WriteAsync(new VolumeChangeBatch(drive, mapped), ct).ConfigureAwait(false);
    }

    List<VolumeChangeEntry> MapEntries(string drive, UsnJournalEntry[] entries)
    {
        var mapped = new List<VolumeChangeEntry>(entries.Length);
        foreach (var entry in entries)
        {
            var path = ResolvePath(drive, entry);
            if (path is not null)
                mapped.Add(new VolumeChangeEntry(path, ClassifyKind(entry)));
        }
        return mapped;
    }

    string? ResolvePath(string drive, UsnJournalEntry entry)
    {
        var index = GetOrAddIndex(drive);

        string resolved;
        if (index.TryGetValue(entry.ParentRecordNumber, out var parentPath))
            resolved = Path.Combine(parentPath, entry.FileName);
        else if (index.TryGetValue(entry.RecordNumber, out var knownPath))
            resolved = knownPath;
        else
            return null;

        index[entry.RecordNumber] = resolved;
        return resolved;
    }

    void IndexRecord(string path, ulong recordNumber)
    {
        var drive = WatchPath.Parse(path).Volume;
        if (drive is null)
            return;

        GetOrAddIndex(drive)[recordNumber] = path;
    }

    Dictionary<ulong, string> GetOrAddIndex(string drive)
    {
        if (!_pathIndexByDrive.TryGetValue(drive, out var index))
            _pathIndexByDrive[drive] = index = new Dictionary<ulong, string>();
        return index;
    }

    static VolumeEntryKind ClassifyKind(UsnJournalEntry entry)
    {
        if (entry.IsCreate)
            return VolumeEntryKind.Created;
        if (entry.IsDelete)
            return VolumeEntryKind.Deleted;
        return VolumeEntryKind.Modified;
    }
}
