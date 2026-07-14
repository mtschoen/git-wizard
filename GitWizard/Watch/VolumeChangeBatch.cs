namespace GitWizard.Watch;

public enum VolumeEntryKind { Modified, Created, Deleted }

public readonly record struct VolumeChangeEntry(string FullPath, VolumeEntryKind Kind);

public sealed record VolumeChangeBatch(string Volume, IReadOnlyList<VolumeChangeEntry> Entries);

public readonly record struct VolumeColdRecord(string Path, ulong RecordId);
