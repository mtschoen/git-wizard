using MFTLib;

namespace GitWizard;

public static class WindowsDefender
{
    static readonly string[] ProcessExclusions =
        ["dotnet.exe", "git.exe", "git-lfs.exe", "git-wizard.exe", "GitWizardUI.exe"];

    const string ElevatedHelperArgument = "--elevated-defender";

    public static string ExclusionsAddedMessage =>
        $"Windows Defender process exclusions added: {string.Join(", ", ProcessExclusions)}.";

    public static bool AddExclusions() => AddExclusions(null);

    /// <summary>
    /// Add Windows Defender process exclusions for GitWizard applications and their external tools.
    /// If already elevated, runs directly. Otherwise, launches an elevated copy of this process.
    /// Falls back to elevated PowerShell if self-elevation is not available (e.g., dotnet run).
    /// </summary>
    /// <returns>True if the exclusions were applied successfully.</returns>
    public static bool AddExclusions(IElevationProvider? elevation)
    {
        if (ElevationUtilities.TryAddDefenderProcessExclusions(
                ProcessExclusions, [ElevatedHelperArgument], elevation))
        {
            GitWizardLog.Log(ExclusionsAddedMessage);
            return true;
        }

        GitWizardLog.Log("Failed to add Windows Defender exclusions.", GitWizardLog.LogType.Error);
        return false;
    }

    /// <summary>
    /// Runs the elevated defender helper when <paramref name="arguments"/> carry the helper flag,
    /// the child mode a self-elevated launch re-enters through.
    /// </summary>
    /// <returns>The process exit code (0 on success, 1 on failure), or null when the arguments do not request the helper.</returns>
    public static int? TryRunElevatedHelper(string[] arguments) => TryRunElevatedHelper(arguments, null);

    internal static int? TryRunElevatedHelper(string[] arguments, IElevationProvider? elevation)
    {
        if (!arguments.Contains(ElevatedHelperArgument))
            return null;

        return AddExclusions(elevation) ? 0 : 1;
    }
}
