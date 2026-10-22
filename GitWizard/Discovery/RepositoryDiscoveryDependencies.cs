using GitWizard.Watch;
using GitWizard.Watch.Linux;
using MFTLib;
using MFTLib.Index;

namespace GitWizard;

public sealed class RepositoryDiscoveryDependencies
{
    public required bool IsWindows { get; init; }
    public required bool IsLinux { get; init; }
    public required Func<string, RootClassification> ClassifyRoot { get; init; }
    public required Func<DiscoveryRequest, string, CancellationToken,
        Task<RecursiveDiscovery>> CrawlRootAsync
    { get; init; }
    public required Func<IReadOnlyList<string>, CacheMode,
        IProgress<IndexScanProgress>?, CancellationToken,
        Task<MftIndexSession>> OpenIndexAsync
    { get; init; }
    public required Func<CancellationToken, Task<string[]?>> ReadListAsync { get; init; }
    public required Func<IReadOnlyList<string>, CancellationToken, Task> WriteListAsync { get; init; }
    public required Func<string, RepositoryCandidateKind, bool> CandidateExists { get; init; }
    public required Func<IVolumeChangeSource> CreateLinuxSource { get; init; }

    public static RepositoryDiscoveryDependencies CreateDefault()
    {
        return new RepositoryDiscoveryDependencies
        {
            IsWindows = OperatingSystem.IsWindows(),
            IsLinux = OperatingSystem.IsLinux(),
            ClassifyRoot = WindowsRootClassifier.Classify,
            CrawlRootAsync = (request, root, token) => EnumerationDiscovery.DiscoverAsync(request, root, token),
            OpenIndexAsync = (drives, cacheMode, progress, token) =>
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("MFT discovery requires Windows.");
                return MftIndexSession.OpenWindowsAsync(drives, BrokerLauncher.Launch, cacheMode, null, progress, token);
            },
            ReadListAsync = token => GitWizardApi.GetCachedRepositoryPathsAsync(token),
            WriteListAsync = (paths, token) => GitWizardApi.SaveCachedRepositoryPathsAsync(paths, token),
            CandidateExists = RepositoryCandidateProbe.Exists,
            CreateLinuxSource = () =>
            {
                if (!OperatingSystem.IsLinux())
                    throw new PlatformNotSupportedException("Linux source requires Linux.");
                return new FanotifyVolumeChangeSource();
            }
        };
    }
}
