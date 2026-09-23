using GitWizard;
using MFTLib.Index;

namespace GitWizardTests.TestSupport;

internal sealed class DiscoveryDependencyRecorder : IDisposable
{
    public List<IReadOnlyList<string>> OpenedDriveSets { get; } = [];
    public List<string> CrawledRoots { get; } = [];
    public Action<string>? CrawlGuard { get; set; }
    public PackedIndexFixture IndexFixture { get; } = new();
    public bool IsWindows { get; set; } = true;
    public Func<IReadOnlyList<string>, MftIndexOpenIntent, IProgress<IndexScanProgress>?, CancellationToken, Task<MftIndexSession>>? CustomOpenIndexAsync { get; set; }

    public RepositoryDiscoveryDependencies BuildDependencies()
    {
        return new RepositoryDiscoveryDependencies
        {
            IsWindows = IsWindows,
            IsLinux = false,
            ClassifyRoot = DefaultClassify,
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

    RootClassification DefaultClassify(string root)
    {
        if (root.StartsWith(@"Z:\", StringComparison.OrdinalIgnoreCase) ||
            root.Contains("shared", StringComparison.OrdinalIgnoreCase) ||
            root.Contains("nonntfs", StringComparison.OrdinalIgnoreCase))
        {
            return new RootClassification(root, RootFileSystem.Recursive, null, null);
        }

        return new RootClassification(root, RootFileSystem.Ntfs, "C", null);
    }

    async Task<MftIndexSession> DefaultOpenIndexAsync(
        IReadOnlyList<string> drives,
        MftIndexOpenIntent intent,
        IProgress<IndexScanProgress>? progress,
        CancellationToken token)
    {
        OpenedDriveSets.Add(drives);
        var driveChars = drives.Select(d => d[0]).ToArray();
        var index = await IndexFixture.OpenIndexAsync(driveChars).ConfigureAwait(false);
        var indexedDrives = drives.Select(d => new IndexedDrive(d[0], IndexFixture.TreePath, 1)).ToList();
        return new MftIndexSession(index, new CountingOwner(), drives, indexedDrives, new Dictionary<string, string>());
    }

    public void Dispose()
    {
        IndexFixture.Dispose();
    }
}
