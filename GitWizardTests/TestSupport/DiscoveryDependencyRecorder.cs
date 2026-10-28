using GitWizard;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

sealed class DiscoveryDependencyRecorder : IDisposable
{
    public List<IReadOnlyList<string>> OpenedDriveSets { get; } = [];
    public List<CacheMode> OpenedCacheModes { get; } = [];
    public List<string> CrawledRoots { get; } = [];
    public Action<string>? CrawlGuard { get; set; }
    public PackedIndexFixture IndexFixture { get; } = new();
    static bool IsWindows => true;
    public Func<IReadOnlyList<string>, CacheMode, IProgress<IndexScanProgress>?, CancellationToken, Task<MftIndexSession>>? CustomOpenIndexAsync { get; set; }

    public RepositoryDiscoveryDependencies BuildDependencies()
    {
        return new RepositoryDiscoveryDependencies
        {
            IsWindows = IsWindows,
            IsLinux = false,
            CrawlRootAsync = (_, root, _) =>
            {
                CrawledRoots.Add(root);
                var reportedRoot = root.Length >= 2 && char.IsLower(root[0]) && root[1] == ':'
                    ? char.ToUpperInvariant(root[0]) + root[1..]
                    : root;
                CrawlGuard?.Invoke(reportedRoot);
                return Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>()));
            },
            OpenIndexAsync = CustomOpenIndexAsync ?? DefaultOpenIndexAsync,
            ReadListAsync = GitWizardApi.GetCachedRepositoryPathsAsync,
            WriteListAsync = GitWizardApi.SaveCachedRepositoryPathsAsync,
            CandidateExists = RepositoryCandidateProbe.Exists,
            CreateLinuxSource = DefaultCreateLinuxSource
        };
    }

    static DiagnosticVolumeChangeSource DefaultCreateLinuxSource() => new();

    async Task<MftIndexSession> DefaultOpenIndexAsync(
        IReadOnlyList<string> drives,
        CacheMode intent,
        IProgress<IndexScanProgress>? progress,
        CancellationToken token)
    {
        OpenedDriveSets.Add(drives);
        OpenedCacheModes.Add(intent);
        // Clone commands can add their shared-clone directory. Supply rows from this owned
        // fixture even for that key; never enumerate a host directory in a routing test.
        var source = SyntheticIndexSource.Create(IndexFixture.Producer);
        var indices = await Task.WhenAll(drives.Select(root => FileIndex.OpenAsync(
            SyntheticVolumeFacts.Apply(new FileIndexOptions
            {
                Root = RootKey.FromDirectory(root),
                CacheMode = CacheMode.Disabled,
                MftSource = source
            }, 1, volume: new VolumeKey('X'), volumeRootDirectory: root), token))).ConfigureAwait(false);
        return new MftIndexSession(indices, new CountingOwner(), drives, new Dictionary<string, string>());
    }

    public void Dispose()
    {
        IndexFixture.Dispose();
    }
}
