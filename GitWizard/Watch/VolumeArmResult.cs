using JetBrains.Annotations;
using MFTLib.Index;

namespace GitWizard.Watch;

public sealed record JournalWarning
{
    public required string DriveLetter { get; init; }
    public required uint VolumeSerial { get; init; }
    public required JournalCheckpointLoss Loss { get; init; }
    public bool HasGrowAction => Loss.Cause == JournalCheckpointLossCause.CheckpointTrimmed
                                 && Loss.SizeThatWouldHaveRetained > Loss.MaximumSize;
    public string? GrowUnavailableReason => HasGrowAction
        ? null
        : Loss.Cause switch
        {
            JournalCheckpointLossCause.JournalRecreated =>
                "The journal was recreated, so no journal size would have kept the saved position.",
            JournalCheckpointLossCause.JournalAdvanced =>
                "The journal moved while the index was closed, so no journal size would have kept the saved position.",
            JournalCheckpointLossCause.CheckpointTrimmed when Loss.SizeThatWouldHaveRetained is null =>
                "The size that would have avoided the rescan is too large to represent.",
            _ => "The journal is already at least as large as the size that would have avoided the rescan."
        };
    public string Message => JournalHintBuilder.BuildHint(Loss);
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
