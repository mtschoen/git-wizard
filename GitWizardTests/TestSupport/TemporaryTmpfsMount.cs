using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace GitWizardTests.Watch.Linux;

/// <summary>
/// A tmpfs mounted on a directory for the lifetime of the instance, so a test can watch a second real
/// filesystem. Disposing unmounts it (lazily when the plain unmount is refused) and fails loudly when it
/// stays mounted, so a test never leaves a mount behind.
/// </summary>
[SupportedOSPlatform("linux")]
sealed class TemporaryTmpfsMount : IDisposable
{
    bool _unmounted;

    TemporaryTmpfsMount(string path) => Path = path;

    public string Path { get; }

    /// <summary>Mounts a small tmpfs on <paramref name="path"/>.</summary>
    /// <returns>The mount, or <see langword="null"/> with the reason when it could not be mounted.</returns>
    public static TemporaryTmpfsMount? TryMount(string path, out string failure)
    {
        Directory.CreateDirectory(path);
        var result = Run("mount", "-t", "tmpfs", "-o", "size=1m", "tmpfs", path);
        if (result.ExitCode == 0)
        {
            failure = string.Empty;
            return new TemporaryTmpfsMount(path);
        }

        failure = result.Output;
        return null;
    }

    /// <summary>Detaches the mount lazily, as a still-open descriptor would otherwise keep it busy.</summary>
    public void DetachLazily()
    {
        var result = Run("umount", "-l", Path);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Lazy unmount of {Path} failed: {result.Output.Trim()}");
        }

        _unmounted = true;
    }

    public void Dispose()
    {
        if (_unmounted)
        {
            return;
        }

        var result = Run("umount", Path);
        if (result.ExitCode == 0)
        {
            return;
        }

        var lazy = Run("umount", "-l", Path);
        throw new InvalidOperationException(
            $"tmpfs at {Path} was not unmounted cleanly ({result.Output.Trim()}); lazy unmount exit code {lazy.ExitCode}.");
    }

    static (int ExitCode, string Output) Run(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (Win32Exception exception)
        {
            return (-1, exception.Message);
        }
    }
}
