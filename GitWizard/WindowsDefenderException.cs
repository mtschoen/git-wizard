using System.Diagnostics;

namespace GitWizard;

public static class WindowsDefenderException
{
    static readonly string[] ProcessExclusions = ["dotnet.exe", "git.exe", "git-lfs.exe", "git-wizard.exe"];

    /// <summary>
    /// Add Windows Defender process exclusions for git and dotnet.
    /// Launches an elevated PowerShell process (triggers UAC prompt).
    /// </summary>
    /// <returns>True if the exclusions were applied successfully.</returns>
    public static bool AddExclusions()
    {
        var commands = ProcessExclusions
            .Select(p => $"Add-MpPreference -ExclusionProcess '{p}'");
        var script = string.Join("; ", commands);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-Command {script}",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true
            };

            var process = Process.Start(startInfo);
            if (process == null)
                return false;

            process.WaitForExit(30000);
            var success = process.ExitCode == 0;

            if (success)
                GitWizardLog.Log("Windows Defender exclusions added successfully.");
            else
                GitWizardLog.Log($"Windows Defender exclusions failed with exit code {process.ExitCode}",
                    GitWizardLog.LogType.Error);

            return success;
        }
        catch (Exception exception)
        {
            GitWizardLog.Log($"Failed to add Windows Defender exclusions: {exception.Message}",
                GitWizardLog.LogType.Error);
            return false;
        }
    }
}
