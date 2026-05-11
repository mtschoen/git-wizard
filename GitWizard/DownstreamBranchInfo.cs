namespace GitWizard;

/// <summary>
/// Information about a local branch that has been merged into another branch
/// and is safe to delete.
/// </summary>
public class DownstreamBranchInfo
{
    /// <summary>
    /// Name of the downstream branch (e.g. "feature/x").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The branch this downstream branch was merged into
    /// (e.g. "main" or "master").
    /// </summary>
    public string MergedInto { get; set; } = string.Empty;
}
