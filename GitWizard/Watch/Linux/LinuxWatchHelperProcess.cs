using System.Diagnostics;

namespace GitWizard.Watch.Linux;

/// <summary>
/// A started helper process as the client sees it: the three redirected pipes and the exit code.
/// The client never kills it (a pkexec child is root and cannot be signalled by its unprivileged
/// parent); closing <see cref="Input"/> is how it is asked to exit.
/// </summary>
public sealed record LinuxWatchHelperProcess(Stream Input, Stream Output, Stream Error, Task<int> Exited);

/// <summary>Starts the helper described by a start info; throws when it cannot be started.</summary>
public delegate LinuxWatchHelperProcess LinuxWatchHelperLauncher(ProcessStartInfo startInfo);

/// <summary>Production start-up for the elevated Linux watch helper.</summary>
public static class LinuxWatchHelperLaunch
{
    static volatile bool _pkexecForbidden;

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

        var startInfo = CreateRedirectedStartInfo("pkexec");
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
        if (_pkexecForbidden && string.Equals(startInfo.FileName, "pkexec", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("pkexec launches are forbidden in this process.");
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The helper process did not start.");
        return new LinuxWatchHelperProcess(process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
            process.StandardError.BaseStream, WaitForExitAsync(process));
    }

    /// <summary>
    /// Makes every later pkexec launch through <see cref="Start"/> fail instead of raising an
    /// authentication dialog. There is no reset; the test assembly calls it once at load.
    /// </summary>
    internal static void ForbidPkexec() => _pkexecForbidden = true;

    static async Task<int> WaitForExitAsync(Process process)
    {
        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}
