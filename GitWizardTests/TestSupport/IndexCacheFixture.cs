using GitWizard;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

sealed class IndexCacheFixture : IDisposable
{
    static readonly byte[] FreeBlockBytes = "invalid but deletable canonical block"u8.ToArray();
    static readonly byte[] LegacyBlockBytes = "legacy hashed-directory block"u8.ToArray();
    static readonly byte[] UnknownBlockBytes = "keep unknown layout"u8.ToArray();

    readonly PackedIndexFixture _files = new();
    public string Home { get; }
    public string Cache { get; }
    public string OwnedPath => SyntheticBlock.CachedPath(Cache, 'X', 1);
    public string FreePath => SyntheticBlock.CachedPath(Cache, 'Y', 1);
    public string LegacyDirectory => Path.Combine(MftIndexCachePolicy.GetCacheRoot(Home), "v1-" + new string('A', 64));
    public string LegacyPath => SyntheticBlock.CachedPath(LegacyDirectory, 'Y', 1);
    public string UnknownDirectory => Path.Combine(MftIndexCachePolicy.GetCacheRoot(Home), "unrecognized");
    public string UnknownPath => SyntheticBlock.CachedPath(UnknownDirectory, 'Z', 1);

    public IndexCacheFixture(string? home = null)
    {
        Home = home ?? _files.DirectoryPath;
        Cache = MftIndexCachePolicy.GetCacheDirectory(Home);
        Directory.CreateDirectory(Cache);
        File.WriteAllBytes(FreePath, FreeBlockBytes);
        File.WriteAllText(Path.Combine(Cache, "unrelated.txt"), "keep");
    }

    public Task<FileIndex> OpenOwnerAsync() => FileIndex.OpenAsync(new FileIndexOptions
    {
        Drives = [new IndexedDrive('X', _files.TreePath, 1, ProducerKind.Enumeration)],
        CacheDirectory = Cache
    }, CancellationToken.None);

    public void SeedLegacyAndUnknownLayout()
    {
        Directory.CreateDirectory(LegacyDirectory);
        File.WriteAllBytes(LegacyPath, LegacyBlockBytes);
        Directory.CreateDirectory(UnknownDirectory);
        File.WriteAllBytes(UnknownPath, UnknownBlockBytes);
    }

    public FileStream? ApplyLegacyOutcome(CachedBlockDeletionOutcome outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (outcome == CachedBlockDeletionOutcome.InUse)
        {
            return new FileStream(LegacyPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        if (outcome == CachedBlockDeletionOutcome.Failed)
        {
            File.SetAttributes(LegacyPath, FileAttributes.ReadOnly);
        }

        return null;
    }

    public void RestoreLegacyAttributes()
    {
        if (File.Exists(LegacyPath))
        {
            File.SetAttributes(LegacyPath, FileAttributes.Normal);
        }
    }

    public void AssertAfterAutomaticMaintenance(CachedBlockDeletionOutcome outcome, string diagnostics)
    {
        var shouldDelete = OperatingSystem.IsWindows() && outcome == CachedBlockDeletionOutcome.Deleted;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(LegacyPath), Is.EqualTo(!shouldDelete), "legacy block selection");
            Assert.That(File.Exists(LegacyPath + ".lock"), Is.EqualTo(OperatingSystem.IsWindows()),
                "Windows cleanup retains its owner-lock file; other platforms do not attempt cleanup");
            Assert.That(File.ReadAllBytes(FreePath), Is.EqualTo(FreeBlockBytes));
            Assert.That(File.ReadAllBytes(UnknownPath), Is.EqualTo(UnknownBlockBytes));
            Assert.That(File.Exists(FreePath + ".lock"), Is.False, "current cache was not inspected");
            Assert.That(File.Exists(UnknownPath + ".lock"), Is.False, "unknown layout was not inspected");
        }
        if (!shouldDelete)
        {
            Assert.That(File.ReadAllBytes(LegacyPath), Is.EqualTo(LegacyBlockBytes));
        }

        if (OperatingSystem.IsWindows() && outcome != CachedBlockDeletionOutcome.Deleted)
        {
            Assert.That(diagnostics, Does.Contain("Warning:")
                .And.Contain("Legacy index cache cleanup incomplete")
                .And.Contain(outcome.ToString())
                .And.Contain(LegacyPath));
            if (outcome == CachedBlockDeletionOutcome.Failed)
            {
                Assert.That(diagnostics, Does.Contain("Failed - "));
            }
        }
        else
        {
            Assert.That(diagnostics, Does.Not.Contain("Legacy index cache cleanup incomplete"));
        }
    }

    public static RepositoryDiscoveryCoordinator CreateCachedOnlyCoordinator() => new(
        new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("Cached paths must not crawl."),
            OpenIndexAsync = (_, _, _, _) => throw new AssertionException("Cached paths must not open an index."),
            ReadListAsync = GitWizardApi.GetCachedRepositoryPathsAsync,
            WriteListAsync = GitWizardApi.SaveCachedRepositoryPathsAsync,
            CandidateExists = RepositoryCandidateProbe.Exists,
            CreateLinuxSource = () => throw new AssertionException("A report must not start a watch.")
        });

    public void Dispose()
    {
        RestoreLegacyAttributes();
        _files.Dispose();
    }
}
