using MFTLib.Index;

namespace GitWizard;

public sealed record MftIndexCacheMaintenanceResult(
    IReadOnlyList<CachedBlockDeletionResult> Deletions,
    IReadOnlyList<CachedBlockStatus> Inventory,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0 &&
        Deletions.All(row => row.Outcome == CachedBlockDeletionOutcome.Deleted);

    public string ToClearText()
    {
        var lines = new List<string>();
        lines.AddRange(Deletions.Select(row => $"{Identity(row.File)}: {row.Outcome}" +
            (row.FailureMessage is null ? string.Empty : $" - {row.FailureMessage}")));

        lines.Add("Cache inspection (point in time):");
        lines.AddRange(Inventory.Select(row => $"{Identity(row.File)}: {row.Availability}" +
            (row.Validation is null ? string.Empty : $" - {row.Validation}")));

        if (Inventory.Count == 0 && Errors.Count == 0)
        {
            lines.Add("No canonical cached blocks remain in the supported policy directories.");
        }

        lines.AddRange(Errors.Select(error => $"Error: {error}"));

        return string.Join(Environment.NewLine, lines);
    }

    static string Identity(CachedBlockFile file) =>
        $"{file.Root}: ({file.VolumeSerial:X8}) [{file.Path}]";
}
