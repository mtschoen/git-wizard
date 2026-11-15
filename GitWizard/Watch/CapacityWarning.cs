namespace GitWizard.Watch;

/// <summary>
/// A drive whose index block could not store at least one journal change, even after growing
/// (<c>RootBlockStatus.IsCapacityExhausted</c>). The watch keeps running and later changes still
/// arrive; only a rescan of the drive restores a complete index.
/// </summary>
public sealed record CapacityWarning
{
    /// <summary>The detail used when the block was already flagged before this watch started.</summary>
    public const string FlaggedBeforeWatchDetail = "the index block was already marked incomplete when watching started";

    public required string DriveLetter { get; init; }

    /// <summary>What could not be stored: the fault's message, or <see cref="FlaggedBeforeWatchDetail" />.</summary>
    public required string Detail { get; init; }

    public string Message =>
        $"Index on {DriveLetter}: could not store a journal change ({Detail}). Watching continues, " +
        $"but searches of {DriveLetter}: can miss that change.";

    public string RescanEffect =>
        $"A rescan rebuilds the index of {DriveLetter}: from the volume and clears this warning.";
}
