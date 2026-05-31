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
    public string Name { get; set; } = string.Empty;

    /// <summary>True when fully merged into the default branch (AheadOfDefault == 0).</summary>
    public bool IsMerged { get; set; }

    /// <summary>The default branch name when merged (safe to delete), else null.</summary>
    public string? MergedInto { get; set; }

    /// <summary>Commits on this branch not reachable from the default branch.</summary>
    public int AheadOfDefault { get; set; }

    /// <summary>Commits in the default branch not reachable from this branch.</summary>
    public int BehindDefault { get; set; }

    /// <summary>Author timestamp of the branch tip's latest commit; null if the branch has no commits.</summary>
    public DateTimeOffset? LastCommitDate { get; set; }

    /// <summary>True when the branch tracks a remote (unpushed local work => false).</summary>
    public bool HasUpstream { get; set; }
}
