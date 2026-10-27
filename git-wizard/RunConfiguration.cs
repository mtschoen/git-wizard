// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

/// <summary>
/// Configuration specified in command line arguments. The default instance is the parse of
/// an empty command line.
/// </summary>
sealed record RunConfiguration
{
    /// <summary>
    /// The command this command line resolves to.
    /// </summary>
    public CommandKind Command { get; init; } = CommandKind.Report;

    /// <summary>
    /// Rebuild the list of repositories (instead of using cache).
    /// </summary>
    public bool RebuildRepositoryList { get; init; }

    /// <summary>
    /// Rebuild the report from scratch (instead of using cache).
    /// </summary>
    public bool RebuildReport { get; init; }

    /// <summary>
    /// Delete repositories.txt and report.json before doing anything else; configuration and
    /// persistent index blocks are preserved.
    /// </summary>
    public bool ClearCache { get; init; }

    /// <summary>
    /// Delete all local files before doing anything else, refusing while the mft-index subtree exists.
    /// </summary>
    public bool DeleteAllLocalFiles { get; init; }

    /// <summary>
    /// Add Windows Defender exclusions before running.
    /// </summary>
    public bool SetupDefender { get; init; }

    /// <summary>
    /// Skip MFT search and use recursive directory scan instead.
    /// </summary>
    public bool NoMft { get; init; }

    /// <summary>
    /// Case-insensitive substring filter applied to repository paths in the output.
    /// </summary>
    public string? FilterPattern { get; init; }

    /// <summary>
    /// Explicit list of repository paths to report on, bypassing discovery.
    /// Can be a file path (newline-separated) or comma-separated inline list.
    /// </summary>
    public string? PathsArgument { get; init; }

    /// <summary>
    /// Print the ordinary report as strict JSON instead of human-readable text.
    /// </summary>
    public bool Json { get; init; }

    /// <summary>
    /// Glob pattern for tag matching in the tags inventory command (null means every tag).
    /// </summary>
    public string? TagMatchPattern { get; init; }

    /// <summary>
    /// Base branch to compare against in the branches inventory command (null means each
    /// repository's default branch).
    /// </summary>
    public string? NotMergedBranch { get; init; }

    /// <summary>
    /// Filter to branches not merged into the base branch.
    /// </summary>
    public bool NotMergedOnly { get; init; }

    /// <summary>
    /// Branches command: keep only unmerged branches with commits on no remote-tracking ref, and
    /// only unmerged stashes.
    /// </summary>
    public bool LocalOnly { get; init; }

    /// <summary>
    /// Prune command: consider local branches.
    /// </summary>
    public bool PruneBranches { get; init; }

    /// <summary>
    /// Prune command: consider stashes.
    /// </summary>
    public bool PruneStashes { get; init; }

    /// <summary>
    /// Prune command: delete eligible branches and stashes (otherwise a dry run).
    /// </summary>
    public bool PruneApply { get; init; }

    /// <summary>
    /// Prune command: base branch to compare against (null means each repository's default).
    /// </summary>
    public string? PruneBaseBranch { get; init; }

    /// <summary>
    /// Refresh the report based on the latest state (otherwise just print out the cached report).
    /// </summary>
    public bool RefreshReport { get; init; } = true;

    /// <summary>
    /// Print stdout JSON in compact form (requires --json for ordinary reports).
    /// </summary>
    public bool Minified { get; init; }

    /// <summary>
    /// Path where the report will be saved to disk (otherwise it is only printed to the console).
    /// </summary>
    public string? SavePath { get; init; }

    /// <summary>
    /// Path to custom configuration file (otherwise global or default configuration is used).
    /// </summary>
    public string? CustomConfigurationPath { get; init; }

    /// <summary>
    /// Include all branches (default + boring) in the per-repo Branches list,
    /// not just actionable branches.
    /// </summary>
    public bool AllBranches { get; init; }

    /// <summary>
    /// When true, skip the expensive per-branch local-commit-count iteration
    /// in each repository refresh. Defaults to false (compute counts).
    /// </summary>
    public bool NoLocalCommitCount { get; init; }

    /// <summary>
    /// True if CLI parsing encountered validation errors (e.g., --save-path with
    /// nonexistent parent directory, --config-path pointing to a missing file).
    /// </summary>
    public bool HasParseError { get; init; }

    /// <summary>
    /// Suppress console output (errors still print to stderr).
    /// </summary>
    public bool Silent { get; init; }
}
