using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GitWizard.Watch.Linux;

/// <summary>
/// The root half of Live on Linux for a desktop process that is not root. The desktop starts its
/// own executable with <see cref="Flag"/> through pkexec; this host runs the in-process fanotify
/// source on its behalf and speaks <see cref="LinuxWatchHelperProtocol"/> over stdin and stdout.
/// It has no window and needs no environment: it reads nothing from and writes nothing under the
/// git-wizard home, and logs only to stderr.
/// </summary>
/// <remarks>
/// After pkexec authenticates, the helper is fully root and the unprivileged parent cannot signal
/// it, so its lifetime is tied to its pipes: it exits as soon as stdin reaches end-of-input or a
/// stdout write fails, and after reporting a terminal <c>died</c> frame.
/// </remarks>
public static class LinuxWatchHelperHost
{
    public const string Flag = "--linux-watch-helper";

    internal const int ExitCodeEndOfInput = 0;
    internal const int ExitCodeNotRoot = 1;
    internal const int ExitCodeOutputFailed = 2;
    internal const int ExitCodeSourceEnded = 3;
    internal const int ExitCodeShutdownTimedOut = 4;

    const int StandardInputDescriptor = 0;
    const int StandardOutputDescriptor = 1;

    /// <summary>
    /// Runs the helper when <paramref name="arguments"/> carries <see cref="Flag"/> and returns its
    /// exit code; returns null for any other launch so the caller continues its normal startup.
    /// </summary>
    public static int? TryRun(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (Array.IndexOf(arguments, Flag) < 0)
        {
            return null;
        }

        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine($"{Flag} is only available on Linux.");
            return ExitCodeNotRoot;
        }

        // Blocking is safe: the helper owns this process from a synchronous Main, with no
        // synchronization context to deadlock on.
        return RunOnLinuxAsync().GetAwaiter().GetResult();
    }

    // Raw descriptors rather than Console streams: the console's stdout stream reports a write to
    // a closed pipe as success, which would leave a helper nobody reads running as root.
    [SupportedOSPlatform("linux")]
    static async Task<int> RunOnLinuxAsync()
    {
        using var inputHandle = new SafeFileHandle(StandardInputDescriptor, false);
        using var outputHandle = new SafeFileHandle(StandardOutputDescriptor, false);
        await using var input = new FileStream(inputHandle, FileAccess.Read, 1);
        await using var output = new FileStream(outputHandle, FileAccess.Write, 1);
        var exitCode = await RunAsync(input, output, Console.Error,
                () => VolumeChangeSourceFactory.LinuxNative.GetEffectiveUserId() == 0,
                () => new FanotifyVolumeChangeSource())
            .ConfigureAwait(false);
        if (exitCode == ExitCodeShutdownTimedOut)
        {
            // The kernel releases descriptors on exit. Do not unwind streams or native handles
            // on this thread while a blocked write, mark or pump can still be using them.
            Environment.Exit(exitCode);
        }

        return exitCode;
    }

    /// <summary>
    /// Serves one helper session over <paramref name="input"/> and <paramref name="output"/>.
    /// Refuses to run unless <paramref name="isRoot"/> holds, before any source is created.
    /// </summary>
    internal static async Task<int> RunAsync(Stream input, Stream output, TextWriter diagnostics,
        Func<bool> isRoot, Func<IVolumeChangeSource> createSource, Func<Task>? startShutdownDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(isRoot);
        ArgumentNullException.ThrowIfNull(createSource);

        if (!isRoot())
        {
            await diagnostics.WriteLineAsync($"{Flag} must run as root (effective user id 0).").ConfigureAwait(false);
            return ExitCodeNotRoot;
        }

        var deadline = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expired = deadline.Task.Unwrap();
        void OnInputEnded() => deadline.SetResult(startShutdownDeadline?.Invoke() ?? Task.Delay(TimeSpan.FromSeconds(1)));

        var stopped = Task.Run(async () =>
        {
            using var session = new LinuxWatchHelperSession(output, TextWriter.Synchronized(diagnostics), createSource());
            return await session.RunAsync(input, OnInputEnded).ConfigureAwait(false);
        });
        return await Task.WhenAny(stopped, expired).ConfigureAwait(false) == stopped
            ? await stopped.ConfigureAwait(false)
            : ExitCodeShutdownTimedOut;
    }
}
