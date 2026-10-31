using GitWizard.Watch;
using GitWizard.Watch.Linux;
using MFTLib;
using MFTLib.Index;

namespace GitWizard.Discovery;

public sealed class RepositoryDiscoveryDependencies
{
    public required bool IsWindows { get; init; }
    public required bool IsLinux { get; init; }
    public required Func<DiscoveryRequest, string, CancellationToken,
        Task<RecursiveDiscovery>> CrawlRootAsync
    { get; init; }
    public Func<IReadOnlyList<string>, CacheMode,
        IProgress<IndexScanProgress>?, CancellationToken,
        Task<MftIndexSession>>? OpenIndexAsync
    { get; init; }
    public Func<FileIndexOptions, FileIndexOptions> ConfigureOptions { get; init; } = options => options;
    public Func<IReadOnlyList<string>> AdditionalCloneRoots { get; init; } = DefaultCloneRoots;
    public required Func<CancellationToken, Task<string[]?>> ReadListAsync { get; init; }
    public required Func<IReadOnlyList<string>, CancellationToken, Task> WriteListAsync { get; init; }
    public required Func<string, RepositoryCandidateKind, bool> CandidateExists { get; init; }
    public required Func<IVolumeChangeSource> CreateLinuxSource { get; init; }

    internal Func<FileIndexSet, IReadOnlyCollection<string>, CancellationToken, IReadOnlyDictionary<string, long>>
        ComputeRepositorySizes
    { get; set; } = RepositorySizeCalculator.Compute;

    internal Task<MftIndexSession> OpenAsync(DiscoveryScope scope, CacheMode cacheMode,
        IProgress<IndexScanProgress>? progress, CancellationToken cancellationToken)
    {
        if (OpenIndexAsync is { } open)
        {
            return open(scope.IndexRoots, cacheMode, progress, cancellationToken);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("MFT discovery requires Windows.");
        }

        return MftIndexSession.OpenSelectedVolumesAsync(scope.IndexRoots, new BrokerSession(BrokerLauncher.Launch),
            new MftIndexSession.CacheSelection(MftIndexCachePolicy.GetCacheDirectory(GitWizardApi.GetLocalFilesPath()), cacheMode),
            progress, cancellationToken,
            options => ConfigureOptions(EnumerationDiscovery.ConfigureWalk(options, scope.Request.Configuration)));
    }

    static IReadOnlyList<string> DefaultCloneRoots()
    {
        var root = GitWizardApi.GetPrCrewReposPath();
        return Directory.Exists(root) ? [root] : [];
    }

    public static RepositoryDiscoveryDependencies CreateDefault(Func<FileIndexOptions, FileIndexOptions>? configureOptions = null,
        Func<IReadOnlyList<string>>? additionalCloneRoots = null)
    {
        return new RepositoryDiscoveryDependencies
        {
            IsWindows = OperatingSystem.IsWindows(),
            IsLinux = OperatingSystem.IsLinux(),
            CrawlRootAsync = (request, root, token) => EnumerationDiscovery.DiscoverAsync(request, root, token,
                configureOptions: configureOptions),
            ConfigureOptions = configureOptions ?? (options => options),
            AdditionalCloneRoots = additionalCloneRoots ?? DefaultCloneRoots,
            ReadListAsync = GitWizardApi.GetCachedRepositoryPathsAsync,
            WriteListAsync = GitWizardApi.SaveCachedRepositoryPathsAsync,
            CandidateExists = RepositoryCandidateProbe.Exists,
            CreateLinuxSource = () => OperatingSystem.IsLinux()
                ? new FanotifyVolumeChangeSource()
                : throw new PlatformNotSupportedException("Linux source requires Linux.")
        };
    }
}
