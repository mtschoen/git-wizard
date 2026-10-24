using JetBrains.Annotations;

namespace GitWizard;

/// <summary>
/// A local branch's relationship to the repository's default branch.
/// Populated into <see cref="GitWizardRepository.Branches"/>.
/// </summary>
/// <remarks>
/// By default the <c>Branches</c> list is filtered to "actionable" branches
/// (see <c>GitWizardReport.BranchScope</c>) - it is NOT necessarily a complete
/// branch inventory. Pass <c>--all-branches</c> to emit every branch.
/// </remarks>
public class BranchInfo
{
    /// <summary>Branch short name (e.g. "feature/x").</summary>
    [UsedImplicitly]
    public string Name { get; set; } = string.Empty;

    /// <summary>True when <see cref="MergedBy"/> is set: the branch's work is already on the base branch.</summary>
    public bool IsMerged { get; init; }

    /// <summary>
    /// The <see cref="GitWizard.MergedBy"/> tier that proved the branch merged into the base
    /// branch, or null. Refresh runs only the cheap tiers (ancestor, content); reports cached
    /// before this field existed load it as null.
    /// </summary>
    [UsedImplicitly]
    public string? MergedBy { get; set; }

    /// <summary>The base branch name when merged (safe to delete), else null.</summary>
    [UsedImplicitly]
    public string? MergedInto { get; set; }

    /// <summary>Commits on this branch not reachable from the default branch.</summary>
    [UsedImplicitly]
    public int AheadOfDefault { get; set; }

    /// <summary>Commits in the default branch not reachable from this branch.</summary>
    [UsedImplicitly]
    public int BehindDefault { get; set; }

    /// <summary>Author timestamp of the branch tip's latest commit; null if the branch has no commits.</summary>
    [UsedImplicitly]
    public DateTimeOffset? LastCommitDate { get; set; }

    /// <summary>True when the branch tracks a remote (unpushed local work => false).</summary>
    [UsedImplicitly]
    public bool HasUpstream { get; set; }
}
