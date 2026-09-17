using MFTLib.Index;

namespace GitWizard.Watch;

internal static class IndexChangeMapping
{
    // Captured locations preserve event history because Entry.Path follows later moves.
    // Entry.IsDirectory is read here, on the watch thread, while the entry handle is valid.
    internal static IReadOnlyList<VolumeChangeEntry> Map(FileChange change) => change.Kind switch
    {
        FileChangeKind.Created => [new(change.Path, VolumeEntryKind.Created)],
        FileChangeKind.Deleted => [new(change.Path, VolumeEntryKind.Deleted)],
        FileChangeKind.Modified => [new(change.Path, VolumeEntryKind.Modified)],
        FileChangeKind.Renamed when change.Entry.IsDirectory && change.PreviousPath is not null =>
        [
            new(change.PreviousPath, VolumeEntryKind.Deleted),
            new(change.Path, VolumeEntryKind.MovedIn)
        ],
        FileChangeKind.Renamed when change.Entry.IsDirectory =>
            [new(change.Path, VolumeEntryKind.MovedIn)],
        FileChangeKind.Renamed when change.PreviousPath is not null =>
        [
            new(change.PreviousPath, VolumeEntryKind.Deleted),
            new(change.Path, VolumeEntryKind.Created)
        ],
        FileChangeKind.Renamed => [new(change.Path, VolumeEntryKind.Created)],
        _ => throw new ArgumentOutOfRangeException(nameof(change), change.Kind, "Unknown file change kind.")
    };
}
