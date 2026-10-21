using MFTLib.Index;

namespace GitWizard.Watch;

static class IndexChangeMapping
{
    // Captured locations preserve event history because Entry.Path follows later moves.
    // Entry.IsDirectory is read here, on the watch thread; when a slot was reused in the same batch,
    // the superseded handle refuses column reads. Unknown types must reach moved-in probing,
    // because Created destinations outside tracked repositories are otherwise ignored.
    internal static IReadOnlyList<VolumeChangeEntry> Map(FileChange change) => change.Kind switch
    {
        FileChangeKind.Created => [new(change.Path, VolumeEntryKind.Created)],
        FileChangeKind.Deleted => [new(change.Path, VolumeEntryKind.Deleted)],
        FileChangeKind.Modified => [new(change.Path, VolumeEntryKind.Modified)],
        FileChangeKind.Renamed when NeedsMovedInProbe(change.Entry) && change.PreviousPath is not null =>
        [
            new(change.PreviousPath, VolumeEntryKind.Deleted),
            new(change.Path, VolumeEntryKind.MovedIn)
        ],
        FileChangeKind.Renamed when NeedsMovedInProbe(change.Entry) =>
            [new(change.Path, VolumeEntryKind.MovedIn)],
        FileChangeKind.Renamed when change.PreviousPath is not null =>
        [
            new(change.PreviousPath, VolumeEntryKind.Deleted),
            new(change.Path, VolumeEntryKind.Created)
        ],
        FileChangeKind.Renamed => [new(change.Path, VolumeEntryKind.Created)],
        _ => throw new ArgumentOutOfRangeException(nameof(change), change.Kind, "Unknown file change kind.")
    };

    static bool NeedsMovedInProbe(FileEntry entry)
    {
        try
        {
            return entry.IsDirectory;
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException && entry.IsDeleted)
        {
            return true;
        }
    }
}
