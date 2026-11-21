using System.Diagnostics;

namespace GitWizard.Watch.Linux;

/// <summary>
/// A started helper process as the client sees it: the three redirected pipes, the exit code, and
/// <see cref="Terminate"/>. Terminate is only meaningful while the process is still pkexec running
/// as the caller (before the helper's hello); once it is root the signal is refused and
/// Terminate must swallow that, and it signals nothing when no process identity was acquired.
/// Closing <see cref="Input"/> is how a root helper is asked to exit; a pkexec still showing its
/// dialog does not read its input, so that does not end it.
/// </summary>
public sealed record LinuxWatchHelperProcess(Stream Input, Stream Output, Stream Error, Task<int> Exited, Action Terminate);

/// <summary>Starts the helper described by a start info; throws when it cannot be started.</summary>
public delegate LinuxWatchHelperProcess LinuxWatchHelperLauncher(ProcessStartInfo startInfo);

/// <summary>Production start-up for the elevated Linux watch helper.</summary>
public static class LinuxWatchHelperLaunch
{
    const string PkexecFileName = "pkexec";

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
        startInfo.ArgumentList.Add("--disable-internal-agent");
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
        return Attach(process);
    }

    internal static LinuxWatchHelperProcess Attach(Process process) => Attach(process, PidfdProcessSignaller.Instance);

    internal static LinuxWatchHelperProcess Attach(Process process, IProcessSignaller signaller)
    {
        // The identity is acquired before anything else can let the child be reaped and its id reused.
        var terminator = ProcessTerminator.Create(process.Id, () => process.HasExited, signaller,
            message => GitWizardLog.Log(message, GitWizardLog.LogType.Verbose));
        // An already-exited child can be disposed synchronously by the waiter.
        var input = process.StandardInput.BaseStream;
        var output = process.StandardOutput.BaseStream;
        var error = process.StandardError.BaseStream;
        var exited = WaitForExitAsync(process, terminator);
        return new LinuxWatchHelperProcess(input, output, error, exited, terminator.Terminate);
    }

    /// <summary>
    /// Makes every later pkexec launch through <see cref="Start"/> fail instead of raising an
    /// authentication dialog. There is no reset; the test assembly calls it once at load.
    /// </summary>
    internal static void ForbidPkexec() => _pkexecForbidden = true;

    static async Task<int> WaitForExitAsync(Process process, ProcessTerminator terminator)
    {
        using (terminator)
        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}
