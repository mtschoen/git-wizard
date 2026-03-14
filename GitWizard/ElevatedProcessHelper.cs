using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace GitWizard;

public static class ElevatedProcessHelper
{
    public static bool IsElevated()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Gets the path to the current executable, or null if running under dotnet.exe (not published).
    /// </summary>
    public static string? GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (processPath == null)
            return null;

        var fileName = Path.GetFileNameWithoutExtension(processPath).ToLowerInvariant();
        if (fileName == "dotnet")
        {
            GitWizardLog.Log("Self-elevation requires a published build. Falling back to directory scan.",
                GitWizardLog.LogType.Warning);
            return null;
        }

        return processPath;
    }

    /// <summary>
    /// Launch an elevated copy of this process with the given arguments.
    /// Returns true if the process completed successfully.
    /// </summary>
    static bool TryRunElevated(string arguments, int timeoutMs = 60000)
    {
        var exePath = GetExecutablePath();
        if (exePath == null)
            return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true
            };

            var process = Process.Start(startInfo);
            if (process == null)
                return false;

            if (!process.WaitForExit(timeoutMs))
            {
                GitWizardLog.Log("Elevated process timed out.", GitWizardLog.LogType.Warning);
                process.Kill();
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User declined UAC prompt
            GitWizardLog.Log("User declined elevation prompt.", GitWizardLog.LogType.Warning);
            return false;
        }
        catch (Exception ex)
        {
            GitWizardLog.Log($"Failed to launch elevated process: {ex.Message}", GitWizardLog.LogType.Error);
            return false;
        }
    }

    /// <summary>
    /// Launch an elevated MFT scan using the given config file. Results are written to a temp file.
    /// </summary>
    /// <param name="configPath">Path to the configuration file with search/ignored paths.</param>
    /// <param name="outputPath">Temp file path where results will be written (one path per line).</param>
    /// <returns>True if the scan completed successfully.</returns>
    public static bool TryRunElevatedMftScan(string configPath, string outputPath)
    {
        GitWizardLog.Log("Launching elevated MFT scan...");
        return TryRunElevated($"--elevated-mft --config-path \"{configPath}\" --output \"{outputPath}\"",
            timeoutMs: 120000);
    }

    /// <summary>
    /// Launch an elevated process to add Windows Defender exclusions.
    /// </summary>
    public static bool TryRunElevatedDefender()
    {
        GitWizardLog.Log("Launching elevated process for Windows Defender exclusions...");
        return TryRunElevated("--elevated-defender", timeoutMs: 30000);
    }
}
