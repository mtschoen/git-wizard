using JetBrains.Annotations;
using MFTLib.Index;

namespace GitWizard.Watch;

public sealed record JournalWarning
{
    public required string DriveLetter { get; init; }
    public required uint VolumeSerial { get; init; }
    public required JournalCheckpointLoss Loss { get; init; }
    // The drive's watch state and recovery flag when the warning was raised; a null state is unknown.
    public WatchState? WatchState { get; init; }
    public bool IsRecoveryStopped { get; init; }
    public bool HasGrowAction => Loss.TryGetGrowthTarget(out _);
    public string? GrowUnavailableReason => Loss.GrowthTargetRefusalReason switch
    {
        null => null,
        JournalGrowthTargetRefusalReason.CheckpointNotTrimmed when Loss.Cause == JournalCheckpointLossCause.JournalRecreated =>
            "The journal was recreated, so no journal size would have kept the saved position.",
        JournalGrowthTargetRefusalReason.CheckpointNotTrimmed =>
            "The journal moved while the index was closed, so no journal size would have kept the saved position.",
        JournalGrowthTargetRefusalReason.RetentionSizeUnavailable =>
            "The size that would have avoided the rescan is too large to represent.",
        JournalGrowthTargetRefusalReason.NonPositiveRetentionSize or JournalGrowthTargetRefusalReason.NonPositiveAllocationDelta =>
            "The recorded journal settings do not provide a valid growth target.",
        _ => "The journal is already at least as large as the size that would have avoided the rescan."
    };
    public string Message => JournalHintBuilder.BuildHint(Loss, WatchState, IsRecoveryStopped);
}

/// <summary>
/// Per-drive failures and journal warnings from <see cref="IVolumeChangeSource.ArmAndCatchUpAsync"/>.
/// A drive present in <see cref="Errors"/> was not successfully armed; watching continues
/// on the remaining, successfully armed drives.
/// </summary>
public sealed record VolumeArmResult
{
    public VolumeArmResult(IReadOnlyDictionary<string, string> errors, IReadOnlyList<JournalWarning> journalWarnings)
    {
        Errors = errors;
        JournalWarnings = journalWarnings;
    }

    [UsedImplicitly] public IReadOnlyDictionary<string, string> Errors { get; init; }
    [UsedImplicitly] public IReadOnlyList<JournalWarning> JournalWarnings { get; init; }
}
