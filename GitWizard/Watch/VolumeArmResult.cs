using JetBrains.Annotations;

namespace GitWizard.Watch;

public sealed record JournalWarning(string DriveLetter, uint VolumeSerial, long MaximumSize, long RecommendedSize, long AllocationDelta)
{
    public string Message => MaximumSize < RecommendedSize
        ? $"warning on {DriveLetter}: USN journal maximum size ({MaximumSize / 1024 / 1024} MB) is below the recommended {RecommendedSize / 1024 / 1024} MB. A wrapped journal faults the live watch and forces a rescan."
        : $"Drive {DriveLetter}: USN journal maximum size ({MaximumSize / 1024 / 1024} MB) meets the recommended {RecommendedSize / 1024 / 1024} MB.";
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
