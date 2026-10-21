using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GitWizard.Watch.Linux;
using MFTLib;
using MFTLib.Index;

namespace GitWizard.Watch;

/// <summary>
/// Platform-aware factory for creating the appropriate <see cref="IVolumeChangeSource"/> implementation
/// (<see cref="IndexVolumeChangeSource"/> on Windows, <see cref="FanotifyVolumeChangeSource"/> on Linux)
/// and checking process elevation status across platforms.
/// </summary>
public static partial class VolumeChangeSourceFactory
{
    internal static Func<bool>? _isWindowsOverride;
    internal static Func<bool>? _isLinuxOverride;
    internal static Func<bool>? _isElevatedOverride;
    internal static Func<IVolumeChangeSource>? _sourceFactoryOverride;

    internal static void ResetToDefaults()
    {
        _isWindowsOverride = null;
        _isLinuxOverride = null;
        _isElevatedOverride = null;
        _sourceFactoryOverride = null;
    }

    /// <summary>
    /// Checks whether the current process has elevated privileges (administrator on Windows,
    /// effective user ID 0 / root on Linux).
    /// </summary>
    public static bool IsElevated()
    {
        if (_isElevatedOverride != null)
        {
            return _isElevatedOverride();
        }

        if (IsWindows())
        {
            return ElevationUtilities.DefaultProvider.IsElevated();
        }

        if (IsLinux())
        {
            return OperatingSystem.IsLinux() && LinuxNative.GetEffectiveUserId() == 0;
        }

        return false;
    }

    /// <summary>
    /// Creates the platform-appropriate <see cref="IVolumeChangeSource"/> implementation.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when the current operating system is not Windows or Linux.
    /// </exception>
    public static IVolumeChangeSource Create() => Create(null);

    /// <summary>
    /// Creates the platform-appropriate <see cref="IVolumeChangeSource"/> implementation,
    /// optionally adopting an existing prepared discovery session on Windows.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when the current operating system is not Windows or Linux.
    /// </exception>
    public static IVolumeChangeSource Create(
        MftIndexSession? preparedIndex,
        Func<string, bool>? brokerLauncher = null)
    {
        if (_sourceFactoryOverride != null)
        {
            return _sourceFactoryOverride();
        }

        if (IsWindows())
        {
            return new IndexVolumeChangeSource((drives, progress, cancellationToken) =>
                OpenWindowsIndexAsync(drives, brokerLauncher, progress, cancellationToken), preparedIndex);
        }

        if (IsLinux() && OperatingSystem.IsLinux())
        {
            return new FanotifyVolumeChangeSource();
        }

        throw new PlatformNotSupportedException("Live mode currently requires Windows or Linux.");
    }

    static Task<MftIndexSession> OpenWindowsIndexAsync(IReadOnlyList<string> drives,
        Func<string, bool>? brokerLauncher, IProgress<IndexScanProgress> progress, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Opening a Windows index requires Windows.");
        }

        return MftIndexSession.OpenWindowsAsync(drives, brokerLauncher ?? BrokerLauncher.Launch,
            null, progress, cancellationToken);
    }

    static bool IsWindows() => _isWindowsOverride?.Invoke() ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    static bool IsLinux() => _isLinuxOverride?.Invoke() ?? RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    [SupportedOSPlatform("linux")]
    internal static partial class LinuxNative
    {
        [LibraryImport("libc", EntryPoint = "geteuid")]
        public static partial uint GetEffectiveUserId();
    }
}
