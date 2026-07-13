namespace GitWizard;

/// <summary>
/// Bundles the optional refresh flags for <see cref="GitWizardReport.GenerateReport"/> so the
/// method stays under the parameter-count limit as new flags are added.
/// </summary>
public class GitWizardReportOptions
{
    /// <summary>When true, fetch from each repository's remotes before computing ahead/behind state.</summary>
    public bool FetchRemotes { get; set; }

    /// <summary>When true, run the expensive <c>git update-index --refresh</c> on each repository.</summary>
    public bool DeepRefresh { get; set; }

    /// <summary>When true, skip MFT-based discovery and walk the filesystem directly.</summary>
    public bool NoMft { get; set; }

    /// <summary>When true, include the default branch and branches sitting at the default tip.</summary>
    public bool AllBranches { get; set; }

    /// <summary>When false, skip the expensive per-branch local-only-commit count (LocalCommitCount/LocalOnlyCommits). Default is true.</summary>
    public bool ComputeLocalCommitCount { get; set; } = true;
}
