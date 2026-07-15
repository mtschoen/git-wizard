using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using MFTLib;

namespace GitWizard.Watch;

/// <summary>
/// Windows <see cref="IVolumeChangeSource"/> over MFTLib's elevated USN journal broker
/// (<see cref="JournalBrokerClient"/>). Live <see cref="UsnJournalEntry"/> batches only carry
/// record numbers and a bare file name, not a full path, so every live entry is resolved back
/// to a full path through a per-drive record-number index seeded from the cold scan: the
/// parent record's indexed path plus the entry's file name, falling back to the entry's own
/// indexed path when its parent was never scanned. Each resolution is folded back into the
/// index so a descendant created later in the same watch (e.g. a ".git" folder inside a
/// directory created moments earlier) also resolves.
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

    JournalBrokerClient? _client;
    IReadOnlyDictionary<string, UsnJournalCursor> _cursorsByDrive =
        new Dictionary<string, UsnJournalCursor>(StringComparer.OrdinalIgnoreCase);
    IReadOnlyDictionary<string, UsnJournalEntry[]> _catchUpEntriesByDrive =
        new Dictionary<string, UsnJournalEntry[]>(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? SourceDied;

    public UsnVolumeChangeSource(Func<string, bool> brokerLaunch)
    {
        _brokerLaunch = brokerLaunch;
    }

    /// <summary>
    /// Spawns and connects the elevated broker, arms + cold-scans <paramref name="volumes"/>,
    /// and seeds the per-drive path index from the resulting scan records.
    /// </summary>
    public async Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        _client = await JournalBrokerClient.SpawnAndConnectAsync(_brokerLaunch, ct).ConfigureAwait(false);
        _client.BrokerDied += reason => SourceDied?.Invoke(reason);

        var scan = await _client.ArmScanAndCatchUpAsync(
            volumes.ToList(), BrokerScanProfile.DirectoryIndexWithGitPointers, ct).ConfigureAwait(false);
        _cursorsByDrive = scan.AdvancedCursors;
        _catchUpEntriesByDrive = scan.CatchUpEntries;

        foreach (var record in scan.Records)
            IndexRecord(record.Path, record.RecordNumber);

        var coldRecords = scan.Records
            .Select(record => new VolumeColdRecord(record.Path, record.RecordNumber))
            .ToList();

        return new VolumeArmResult(coldRecords, scan.Errors);
    }

    /// <summary>
    /// Starts the live watch on every drive armed by <see cref="ArmAndCatchUpAsync"/> and
    /// streams one <see cref="VolumeChangeBatch"/> per drive batch, in arrival order across
    /// drives (each drive is pumped concurrently into a shared channel).
    /// </summary>
    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (_client is null)
            throw new InvalidOperationException("ArmAndCatchUpAsync must be called before WatchAsync.");

        await _client.SendStartWatchAsync(_cursorsByDrive, ct).ConfigureAwait(false);
        var batchSource = _client.CreateBatchSource();
        var channel = Channel.CreateUnbounded<VolumeChangeBatch>();

        var pumpTask = PumpAllDrivesAsync(batchSource, channel.Writer, ct);
        await foreach (var batch in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return batch;

        await pumpTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync().ConfigureAwait(false);
    }

    async Task PumpAllDrivesAsync(
        JournalBatchSource batchSource, ChannelWriter<VolumeChangeBatch> writer, CancellationToken ct)
    {
        try
        {
            var pumps = _cursorsByDrive.Select(pair => PumpDriveAsync(batchSource, pair.Key, pair.Value, writer, ct));
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    async Task PumpDriveAsync(
        JournalBatchSource batchSource, string drive, UsnJournalCursor cursor,
        ChannelWriter<VolumeChangeBatch> writer, CancellationToken ct)
    {
        await EmitCatchUpBatchAsync(drive, writer, ct).ConfigureAwait(false);

        await foreach (var (entries, _) in batchSource(drive, cursor, ct).ConfigureAwait(false))
        {
            var mapped = MapEntries(drive, entries);
            if (mapped.Count > 0)
                await writer.WriteAsync(new VolumeChangeBatch(drive, mapped), ct).ConfigureAwait(false);
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
        var drive = GitWizardApi.GetDriveLetter(path);
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
