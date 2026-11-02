namespace GitWizard;

/// <summary>
/// Cached divergence and cheap-merge outcome for one local branch tip against the repository's
/// comparison and base branch tips.
/// </summary>
public sealed record BranchDivergenceCacheEntry
{
    public string BranchTipSha { get; init; } = string.Empty;
    public string? ComparisonTipSha { get; init; }
    public string? BaseTipSha { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public string? MergedBy { get; init; }

    internal (string BranchTipSha, string? ComparisonTipSha, string? BaseTipSha) Key =>
        (BranchTipSha, ComparisonTipSha, BaseTipSha);
}
