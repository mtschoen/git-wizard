using MFTLib.Index;

namespace GitWizard;

public sealed record MftIndexCacheMaintenanceResult(
    IReadOnlyList<CachedBlockDeletionResult> Deletions,
    IReadOnlyList<CachedBlockStatus> Inventory,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0 &&
        Deletions.All(row => row.Outcome == CachedBlockDeletionOutcome.Deleted);

    public string ToDisplayText()
    {
        var lines = new List<string>();
        foreach (var row in Deletions)
        {
            lines.Add($"{Identity(row.File)}: {row.Outcome}" +
                (row.FailureReason is null ? string.Empty : $" - {row.FailureReason}"));
        }

        lines.Add("Cache inspection (point in time):");
        foreach (var row in Inventory)
        {
            lines.Add($"{Identity(row.File)}: {row.Availability}" +
                (row.Validation is null ? string.Empty : $" - {row.Validation}"));
        }

        if (Inventory.Count == 0 && Errors.Count == 0)
        {
            lines.Add("No canonical cached blocks remain in the supported policy directories.");
        }

        foreach (var error in Errors)
        {
            lines.Add($"Error: {error}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    static string Identity(CachedBlockFile file) =>
        $"{file.DriveLetter}: ({file.VolumeSerial:X8}) [{file.Path}]";
}
