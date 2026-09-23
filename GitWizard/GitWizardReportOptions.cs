using JetBrains.Annotations;

namespace GitWizard;

/// <summary>
/// Bundles the optional refresh flags for <see cref="GitWizardReport.GenerateReportAsync"/> so the
/// method stays under the parameter-count limit as new flags are added.
/// </summary>
public class GitWizardReportOptions
{
    /// <summary>When true, fetch from each repository's remotes before computing ahead/behind state.</summary>
    [UsedImplicitly]
    public bool FetchRemotes { get; set; }

    /// <summary>When true, run the expensive <c>git update-index --refresh</c> on each repository.</summary>
    [UsedImplicitly]
    public bool DeepRefresh { get; set; }

    /// <summary>When true, skip MFT-based discovery and walk the filesystem directly.</summary>
    [UsedImplicitly]
    public bool NoMft { get; set; }

    /// <summary>When true, include the default branch and branches sitting at the default tip.</summary>
    [UsedImplicitly]
    public bool AllBranches { get; set; }

    /// <summary>When false, skip the expensive per-branch local-only-commit count (LocalCommitCount/LocalOnlyCommits). Default is true.</summary>
    public bool ComputeLocalCommitCount { get; set; } = true;

    /// <summary>When true, retain any elevated MftIndexSession created during MFT discovery on the report.</summary>
    [UsedImplicitly]
    public bool RetainDiscoveryIndex { get; set; }

    /// <summary>Purpose of the discovery pass (default is Ordinary).</summary>
    public DiscoveryPurpose DiscoveryPurpose { get; set; } = DiscoveryPurpose.Ordinary;

    /// <summary>When true, allows reading from the cached repository list.</summary>
    public bool AllowRepositoryList { get; set; } = true;

    /// <summary>Injected coordinator for testing and routing.</summary>
    public RepositoryDiscoveryCoordinator? DiscoveryCoordinator { get; set; }
}
