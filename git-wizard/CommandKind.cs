// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

/// <summary>
/// The one thing a command line asks git-wizard to do. When several mode flags appear together
/// the parser keeps the one that runs first, so every parsed configuration names exactly one kind.
/// </summary>
enum CommandKind
{
    /// <summary>
    /// Generate or print the repository report (the default when no mode flag is given).
    /// </summary>
    Report = 0,

    /// <summary>
    /// Output the compact machine-sweep JSON contract.
    /// </summary>
    Sweep = 1,

    /// <summary>
    /// Output the tags inventory JSON contract.
    /// </summary>
    Tags = 2,

    /// <summary>
    /// Output the branches inventory JSON contract.
    /// </summary>
    Branches = 3,

    /// <summary>
    /// Print discovered repository paths and exit without refreshing.
    /// </summary>
    ScanOnly = 4,

    /// <summary>
    /// Merge the repositories named by --paths into the existing report at --save-path.
    /// </summary>
    Merge = 5,

    /// <summary>
    /// Watch tracked repositories for changes and print a line per change.
    /// </summary>
    Watch = 6,

    /// <summary>
    /// McMaster already printed help or version text; exit without running anything.
    /// </summary>
    HelpPrinted = 7,

    /// <summary>
    /// Inspect the current MFT cache policy and exit without discovery or a broker.
    /// </summary>
    IndexCacheStatus = 8,

    /// <summary>
    /// Clear unlocked MFT index blocks, show per-drive results, then exit.
    /// </summary>
    ClearIndexCache = 9,

    /// <summary>
    /// Show the size of the GitWizard local files folder and exit.
    /// </summary>
    DbSize = 10,

    /// <summary>
    /// Output the prune JSON contract, deleting merged branches only with --apply; merged stashes are reported, never dropped.
    /// </summary>
    Prune = 11
}
