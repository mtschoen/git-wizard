using JetBrains.Annotations;

namespace GitWizard.Watch;

public enum VolumeEntryKind { Modified = 0, Created = 1, Deleted = 2 }

public readonly record struct VolumeChangeEntry(
    [property: UsedImplicitly] string FullPath,
    [property: UsedImplicitly] VolumeEntryKind Kind);

public sealed record VolumeChangeBatch(
    [property: UsedImplicitly] string Volume,
    [property: UsedImplicitly] IReadOnlyList<VolumeChangeEntry> Entries);
