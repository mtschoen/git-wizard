using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GitWizard.Watch.Linux;
using MFTLib;

namespace GitWizard.Watch;

/// <summary>
/// Platform-aware factory for creating the appropriate <see cref="IVolumeChangeSource"/> implementation
/// (<see cref="UsnVolumeChangeSource"/> on Windows, <see cref="FanotifyVolumeChangeSource"/> on Linux)
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
            return _isElevatedOverride();

        if (IsWindows())
            return ElevationUtilities.IsElevated();

        if (IsLinux())
            return OperatingSystem.IsLinux() && LinuxNative.GetEffectiveUserId() == 0;

        return false;
    }

    /// <summary>
    /// Creates the platform-appropriate <see cref="IVolumeChangeSource"/> implementation.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when the current operating system is not Windows or Linux.
    /// </exception>
    public static IVolumeChangeSource Create() => Create(null, null);

    /// <summary>
    /// Creates the platform-appropriate <see cref="IVolumeChangeSource"/> implementation,
    /// optionally adopting an existing prepared discovery session on Windows.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when the current operating system is not Windows or Linux.
    /// </exception>
    public static IVolumeChangeSource Create(
        JournalBrokerScanSession? preparedSession,
        IReadOnlyList<ScanRecord>? scanRecords,
        IReadOnlyList<string>? preparedDrives = null,
        Func<string, bool>? brokerLauncher = null)
    {
        if (_sourceFactoryOverride != null)
            return _sourceFactoryOverride();

        if (IsWindows() && OperatingSystem.IsWindows())
        {
            brokerLauncher ??= BrokerLauncher.Launch;
            if (preparedSession != null && scanRecords != null)
                return new UsnVolumeChangeSource(preparedSession, scanRecords, brokerLauncher, preparedDrives);

            return new UsnVolumeChangeSource(brokerLauncher);
        }

        if (IsLinux() && OperatingSystem.IsLinux())
        {
            return new FanotifyVolumeChangeSource();
        }

        throw new PlatformNotSupportedException("Live mode currently requires Windows or Linux.");
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
