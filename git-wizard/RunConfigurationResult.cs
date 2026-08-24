// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

sealed record RunConfigurationResult(
    bool RebuildRepositoryList,
    bool RebuildReport,
    bool ClearCache,
    bool DeleteAllLocalFiles,
    bool SetupDefender,
    bool ScanOnly,
    bool NoMft,
    string? FilterPattern,
    string? PathsArgument,
    bool Summary,
    bool Sweep,
    bool Merge,
    bool RefreshReport,
    bool Minified,
    string? SavePath,
    string? CustomConfigurationPath,
    bool DbSize,
    bool AllBranches,
    bool Watch,
    bool NoLocalCommitCount,
    bool HasError)
{
    /// <summary>
    /// True when McMaster already printed help or version text and the caller should exit
    /// immediately without running any report generation (e.g., --help, --version).
    /// </summary>
    public bool ExitRequested { get; init; }

    /// <summary>
    /// True when the tags subcommand was invoked.
    /// </summary>
    public bool Tags { get; init; }

    /// <summary>
    /// The glob pattern for tag matching (defaults to *).
    /// </summary>
    public string? TagMatchPattern { get; init; }

    /// <summary>
    /// True when the branches subcommand was invoked.
    /// </summary>
    public bool Branches { get; init; }

    /// <summary>
    /// The base branch to compare local branches against (null means use each repository's default branch).
    /// </summary>
    public string? NotMergedBranch { get; init; }

    /// <summary>
    /// True when filtering to only branches not merged into the base branch.
    /// </summary>
    public bool NotMergedOnly { get; init; }
}
