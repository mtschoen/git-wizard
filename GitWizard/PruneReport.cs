using JetBrains.Annotations;

namespace GitWizard;

/// <summary>
/// Result of a <c>prune</c> run: every branch and stash considered, and what was (or would be)
/// done with it.
/// </summary>
[Serializable]
public sealed class PruneReport
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>False for a dry run (nothing deleted); true when deletions were attempted.</summary>
    public bool Applied { get; set; }

    public List<PruneCandidate> Candidates { get; set; } = new();

    /// <summary>Repositories that could not be inspected, with the reason.</summary>
    public List<PruneRepositoryError> Errors { get; set; } = new();
}

/// <summary>
/// A branch or stash considered for pruning.
/// </summary>
[Serializable]
public sealed class PruneCandidate
{
    public const string BranchKind = "branch";
    public const string StashKind = "stash";
    public const string WouldDelete = "would-delete";
    public const string Deleted = "deleted";
    public const string Skipped = "skipped";

    public string Repository { get; set; } = string.Empty;

    /// <summary><see cref="BranchKind"/> or <see cref="StashKind"/>.</summary>
    public string Kind { get; set; } = BranchKind;

    /// <summary>Branch name, or <c>stash@{n}</c> as listed before any drop in this run.</summary>
    public string Name { get; set; } = string.Empty;

    public string Sha { get; set; } = string.Empty;

    /// <summary>The <see cref="GitWizard.MergedBy"/> tier, or null when not merged.</summary>
    public string? MergedBy { get; set; }

    /// <summary><see cref="WouldDelete"/>, <see cref="Deleted"/> or <see cref="Skipped"/>.</summary>
    public string Action { get; set; } = Skipped;

    /// <summary>Why the candidate was skipped (see <see cref="PruneSkipReasons"/>); null otherwise.</summary>
    public string? SkipReason { get; set; }

    /// <summary>The stash message; null for branches.</summary>
    [UsedImplicitly]
    public string? Message { get; set; }
}

/// <summary>
/// A repository the prune run could not inspect.
/// </summary>
[Serializable]
public sealed class PruneRepositoryError
{
    public string Repository { get; set; } = string.Empty;

    [UsedImplicitly]
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// The <see cref="PruneCandidate.SkipReason"/> values.
/// </summary>
public static class PruneSkipReasons
{
    public const string NotMerged = "not-merged";
    public const string BaseBranch = "base-branch";
    public const string CheckedOut = "checked-out";
    public const string CheckedOutInWorktree = "checked-out-in-worktree";
    public const string WorktreeListFailed = "worktree-list-failed";
    public const string StashMoved = "stash-moved";
    public const string DeleteFailed = "delete-failed";
    public const string NotConfirmed = "not-confirmed";
}

/// <summary>
/// What a prune run considers and whether it deletes.
/// </summary>
/// <param name="Branches">Consider local branches.</param>
/// <param name="Stashes">Consider stashes.</param>
/// <param name="Apply">Delete eligible candidates; false is a dry run.</param>
/// <param name="BaseBranchName">Explicit base branch; null resolves each repository's default.</param>
/// <param name="OnlyShas">
/// When set, only branches and stashes whose SHA is listed are eligible; every other one is
/// skipped as <see cref="PruneSkipReasons.NotConfirmed"/> without running merge detection. Lets a
/// caller apply exactly the candidates a person confirmed from an earlier dry run.
/// </param>
public sealed record PruneOptions(
    bool Branches = true,
    bool Stashes = true,
    bool Apply = false,
    string? BaseBranchName = null,
    IReadOnlySet<string>? OnlyShas = null)
{
    internal Action<PruneCandidate>? BeforeDelete { get; init; }
}
