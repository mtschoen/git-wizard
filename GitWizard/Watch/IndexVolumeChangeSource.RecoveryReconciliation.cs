using MFTLib.Index;

namespace GitWizard.Watch;

// MFTLib recovers a drive by publishing a freshly scanned block, and a block swap raises no
// Changed events: repositories created or deleted while the drive's watch was down would never
// reach the repository watch. The source therefore records the drive's repository markers when
// the recovery starts and, once the recovered watch has caught up, delivers the difference as
// ordinary change entries, so RepositoryWatchService applies its own membership and refresh
// rules to them exactly as it does to live journal changes.
public sealed partial class IndexVolumeChangeSource
{
    // By drive: the repository marker paths of the block a recovery replaces. Guarded by
    // _watchStateLock; the first fault of a recovery episode records it, and the episode's
    // reconciliation or the drive's exclusion consumes it.
    readonly Dictionary<char, HashSet<string>> _recoveryBaselines = [];

    // Held by OnChanged for each delivery and by a reconciliation from reading the recovered
    // block to queuing its batch, so a live change applied after that read is always delivered
    // after the reconciliation it would otherwise overtake.
    readonly Lock _deliveryLock = new();

    /// <summary>Runs inside the delivery gate between reading the recovered block and queuing its reconciliation.</summary>
    internal Action<char>? ReconciliationCapturedForTest { get; set; }

    /// <summary>Runs after marker selection, immediately before each borrowed handle's path is read.</summary>
    internal Action<FileEntry>? RepositoryMarkerReadingForTest { get; set; }

    // The baseline must come from the block the recovery replaces, so it is read in the
    // WatchFaulted handler: MFTLib starts the recovery only after the handler returns.
    void CaptureRecoveryBaseline(char drive)
    {
        lock (_watchStateLock)
        {
            if (_recoveryBaselines.ContainsKey(drive))
            {
                return;
            }
        }
        if (CaptureRepositoryMarkers(drive) is not { } markers)
        {
            return;
        }

        lock (_watchStateLock)
        {
            _recoveryBaselines.TryAdd(drive, markers);
        }
    }

    // False when the drive's block could not be read (a newer recovery replaced it under the
    // read), in which case nothing was delivered.
    bool DeliverRecoveryReconciliation(char drive, HashSet<string> baseline)
    {
        lock (_deliveryLock)
        {
            HashSet<string>? current;
            try
            {
                current = CaptureRepositoryMarkers(drive);
            }
            catch (ObjectDisposedException exception)
            {
                GitWizardLog.Log($"Live watch on {drive}: the recovered block was replaced while it was reconciled: " +
                                 exception.Message, GitWizardLog.LogType.Verbose);
                return false;
            }
            if (current is null)
            {
                return false;
            }

            ReconciliationCapturedForTest?.Invoke(drive);
            if (BuildRecoveryReconciliation(drive, baseline, current) is { } reconciliation)
            {
                Deliver(reconciliation);
            }

            return true;
        }
    }

    /// <summary>
    /// The recovered drive's marker changes against <paramref name="baseline" />: a marker that
    /// disappeared is Deleted, a new one is Created, and a surviving one is Modified so its
    /// repository is refreshed, since its own changes during the gap were never observed.
    /// </summary>
    static VolumeChangeBatch? BuildRecoveryReconciliation(char drive, HashSet<string> baseline, HashSet<string> current)
    {
        var entries = baseline.Where(marker => !current.Contains(marker))
            .Select(marker => new VolumeChangeEntry(marker, VolumeEntryKind.Deleted))
            .Concat(current.Where(marker => !baseline.Contains(marker))
                .Select(marker => new VolumeChangeEntry(marker, VolumeEntryKind.Created)))
            .Concat(current.Where(baseline.Contains)
                .Select(marker => new VolumeChangeEntry(marker, VolumeEntryKind.Modified)))
            .ToArray();
        return entries.Length == 0 ? null : new VolumeChangeBatch(drive.ToString(), entries);
    }

    // Marker paths in the drive-letter form every batch carries. Entries are read into strings
    // at once, so no borrowed handle outlives the block it came from.
    HashSet<string>? CaptureRepositoryMarkers(char drive)
    {
        var owner = _owner;
        if (owner is null)
        {
            return null;
        }

        _driveRealRoots.TryGetValue(drive, out var realRoot);
        var markers = new HashSet<string>(WatchPath.StringComparer);
        foreach (var entry in GitWizardApi.SelectGitEntries(owner.Index, includeGitFiles: true))
        {
            if (entry.Drive.Letter != drive)
            {
                continue;
            }

            string path;
            try
            {
                RepositoryMarkerReadingForTest?.Invoke(entry);
                path = entry.Path;
                // MFTLib validates identity before each read; reuse can still race the column read.
                if (entry.IsDeleted)
                {
                    continue;
                }
            }
            catch (InvalidOperationException exception) when (exception is not ObjectDisposedException && entry.IsDeleted)
            {
                // Only a superseded handle is absent; path failures must decline the whole capture.
                continue;
            }

            markers.Add(realRoot is null ? path : RewriteToDriveLetterRoot(path, realRoot, drive));
        }

        return markers;
    }
}
