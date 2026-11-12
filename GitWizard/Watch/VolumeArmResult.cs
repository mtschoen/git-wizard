using JetBrains.Annotations;
using MFTLib;
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
    public long? MaximumRecommendedSize { get; init; }
    public long? CurrentMaximumSize { get; init; }
    public UsnJournalGrowthOutcome? LastGrowthOutcome { get; init; }
    long EffectiveMaximumSize => CurrentMaximumSize ?? Loss.JournalSettings.MaximumSize;
    long? AppliedMaximumSize =>
        LastGrowthOutcome is UsnJournalGrowthOutcome.Unchanged or UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget
            ? null
            : CurrentMaximumSize;
    public bool HasGrowAction =>
        CanAttemptDefaultGrow() &&
        Loss.TryGetGrowthTarget(out var target) &&
        EffectiveMaximumSize < target.MaximumSize &&
        (MaximumRecommendedSize is not { } maximumRecommended ||
         EffectiveMaximumSize < maximumRecommended);
    public bool CanGrowAboveMaximumRecommended =>
        CanAttemptOverrideGrow() &&
        Loss.TryGetGrowthTarget(out var target) &&
        MaximumRecommendedSize is { } maximumRecommended &&
        target.MaximumSize > maximumRecommended &&
        EffectiveMaximumSize < target.MaximumSize;
    public string? GrowUnavailableReason
    {
        get
        {
            if (Loss.TryGetGrowthTarget(out var target))
            {
                if (LastGrowthOutcome == UsnJournalGrowthOutcome.Unchanged)
                {
                    return FormatUnchangedReason();
                }

                if (EffectiveMaximumSize >= target.MaximumSize ||
                    LastGrowthOutcome is UsnJournalGrowthOutcome.GrewAsRequested or UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride)
                {
                    return "The journal is already at least as large as the size that would have avoided the rescan.";
                }

                if (MaximumRecommendedSize is { } maximumRecommended &&
                    (EffectiveMaximumSize >= maximumRecommended ||
                     LastGrowthOutcome == UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended) &&
                    target.MaximumSize > maximumRecommended)
                {
                    return $"The journal is already at least as large as the recommended maximum of {ByteSizeFormatter.Format(maximumRecommended)} (needed {ByteSizeFormatter.Format(target.MaximumSize)}).";
                }

                return null;
            }

            return Loss.GrowthTargetRefusalReason switch
            {
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
        }
    }
    public string Message => JournalHintBuilder.BuildHint(Loss, WatchState, IsRecoveryStopped, MaximumRecommendedSize, AppliedMaximumSize);

    public string FormatUnchangedReason() =>
        $"Windows accepted the request but kept the journal at {ByteSizeFormatter.Format(EffectiveMaximumSize)}.";

    bool CanAttemptDefaultGrow()
    {
        if (LastGrowthOutcome is not { } outcome)
        {
            return true;
        }

#pragma warning disable CS8524 // Omit discard arm so CS8509 fails the build when a new UsnJournalGrowthOutcome is added.
        return outcome switch
        {
            UsnJournalGrowthOutcome.GrewAsRequested => false,
            UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended => false,
            UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride => false,
            UsnJournalGrowthOutcome.Unchanged => false,
            UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget => true
        };
#pragma warning restore CS8524
    }

    bool CanAttemptOverrideGrow()
    {
        if (LastGrowthOutcome is not { } outcome)
        {
            return true;
        }

#pragma warning disable CS8524 // Omit discard arm so CS8509 fails the build when a new UsnJournalGrowthOutcome is added.
        return outcome switch
        {
            UsnJournalGrowthOutcome.GrewAsRequested => false,
            UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride => false,
            UsnJournalGrowthOutcome.Unchanged => false,
            UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended => true,
            UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget => true
        };
#pragma warning restore CS8524
    }
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
