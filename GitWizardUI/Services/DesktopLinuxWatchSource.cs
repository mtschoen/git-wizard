using System.Runtime.Versioning;
using GitWizard.Discovery;
using GitWizard.Watch;
using GitWizard.Watch.Linux;

namespace GitWizardUI.Services;

/// <summary>
/// Chooses the Linux Live source for the desktop app: the in-process fanotify source when root,
/// otherwise a pkexec-started root helper running this executable. Only GitWizardUI dispatches the
/// helper flag, so the library defaults stay in-process and the desktop opts in here.
/// </summary>
internal static class DesktopLinuxWatchSource
{
    [SupportedOSPlatform("linux")]
    public static IVolumeChangeSource Create(bool isRoot, string? executablePath) =>
        isRoot ? new FanotifyVolumeChangeSource() : ElevatedHelperVolumeChangeSource.CreatePkexec(executablePath);

    public static RepositoryDiscoveryCoordinator CreateCoordinator()
    {
        var defaults = RepositoryDiscoveryDependencies.CreateDefault();
        return new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = defaults.IsWindows,
            IsLinux = defaults.IsLinux,
            CrawlRootAsync = defaults.CrawlRootAsync,
            ConfigureOptions = defaults.ConfigureOptions,
            OpenIndexAsync = defaults.OpenIndexAsync,
            ReadListAsync = defaults.ReadListAsync,
            WriteListAsync = defaults.WriteListAsync,
            CandidateExists = defaults.CandidateExists,
            CreateLinuxSource = () => OperatingSystem.IsLinux()
                ? Create(VolumeChangeSourceFactory.IsElevated(), Environment.ProcessPath)
                : throw new PlatformNotSupportedException("Linux source requires Linux.")
        });
    }
}
