using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GitWizard.Watch.Linux;

/// <summary>
/// A started helper process as the client sees it: the three redirected pipes, the exit code, and
/// <see cref="Terminate"/>. Terminate is only meaningful while the process is still pkexec running
/// as the caller (before the helper's hello); once it is root the signal is refused and
/// Terminate must swallow that. Closing <see cref="Input"/> is how a root helper is asked to exit.
/// </summary>
public sealed record LinuxWatchHelperProcess(Stream Input, Stream Output, Stream Error, Task<int> Exited, Action Terminate);

/// <summary>Starts the helper described by a start info; throws when it cannot be started.</summary>
public delegate LinuxWatchHelperProcess LinuxWatchHelperLauncher(ProcessStartInfo startInfo);

/// <summary>Production start-up for the elevated Linux watch helper.</summary>
public static partial class LinuxWatchHelperLaunch
{
    const string PkexecFileName = "pkexec";
    const int TerminateSignal = 15;

    static volatile bool _pkexecForbidden;

    /// <summary>True when <paramref name="executablePath"/> is the dotnet host rather than an app executable.</summary>
    public static bool IsDotnetHost(string executablePath) =>
        string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the pkexec command line that runs <paramref name="executablePath"/> in helper mode,
    /// or null when the current executable is unknown.
    /// </summary>
    public static ProcessStartInfo? CreatePkexecStartInfo(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return null;
        }

        var startInfo = CreateRedirectedStartInfo(PkexecFileName);
        startInfo.ArgumentList.Add(executablePath);
        startInfo.ArgumentList.Add(LinuxWatchHelperHost.Flag);
        return startInfo;
    }

    /// <summary>A start info for <paramref name="fileName"/> with all three standard streams redirected.</summary>
    public static ProcessStartInfo CreateRedirectedStartInfo(string fileName) => new(fileName)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    /// <summary>Starts the process with <see cref="Process.Start(ProcessStartInfo)"/>.</summary>
    public static LinuxWatchHelperProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (_pkexecForbidden && string.Equals(Path.GetFileName(startInfo.FileName), PkexecFileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("pkexec launches are forbidden in this process.");
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The helper process did not start.");
        var processId = process.Id;
        var exited = WaitForExitAsync(process);
        return new LinuxWatchHelperProcess(process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
            process.StandardError.BaseStream, exited, () => TerminateUnlessExited(processId, exited));
    }

    // SIGTERM, the signal pkexec ends on with its dialog closing; refused (EPERM) once the process
    // has become root, which is the expected outcome after authentication.
    static void TerminateUnlessExited(int processId, Task exited)
    {
        if (exited.IsCompleted || !OperatingSystem.IsLinux())
        {
            return;
        }

        if (LinuxNative.SendSignal(processId, TerminateSignal) != 0)
        {
            GitWizardLog.Log($"Ending the pkexec process was refused: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}",
                GitWizardLog.LogType.Verbose);
        }
    }

    /// <summary>
    /// Makes every later pkexec launch through <see cref="Start"/> fail instead of raising an
    /// authentication dialog. There is no reset; the test assembly calls it once at load.
    /// </summary>
    internal static void ForbidPkexec() => _pkexecForbidden = true;

    static partial class LinuxNative
    {
        [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
        public static partial int SendSignal(int processId, int signal);
    }

    static async Task<int> WaitForExitAsync(Process process)
    {
        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}
