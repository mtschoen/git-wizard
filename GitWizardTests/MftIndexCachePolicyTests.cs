using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests;

[TestFixture]
public class MftIndexCachePolicyTests
{
    // One row per CacheTag version ever shipped. Changing scan retention changes
    // ScanShape and fails CurrentScanShapeIsPinnedToItsCacheTagVersion until the version in
    // MftIndexCachePolicy is incremented and a new row is added here. Never edit an existing row.
    static readonly Dictionary<uint, string> ScanShapeByVersion = new()
    {
        [1] = "DirectoryIndex|.git,HEAD",
        [2] = "directories|.git,HEAD"
    };

    [Test]
    public void CurrentScanShapeIsPinnedToItsCacheTagVersion()
    {
        var tag = MftIndexCachePolicy.CacheTag;
        Assert.That(ScanShapeByVersion, Does.ContainKey(SyntheticCacheTag.GetVersion(tag)),
            "Add a row for the new CacheTag version to ScanShapeByVersion.");
        Assert.That(MftIndexCachePolicy.ScanShape, Is.EqualTo(ScanShapeByVersion[SyntheticCacheTag.GetVersion(tag)]),
            "Scan retention changed: increment CacheTagVersion in MftIndexCachePolicy.");
        Assert.That(SyntheticCacheTag.GetVersion(tag), Is.EqualTo(ScanShapeByVersion.Keys.Max()));
    }

    [Test]
    public void PolicyDefinesTheDiscoveryScanAndOneCacheDirectory()
    {
        using var files = new PackedIndexFixture();
        Assert.That(MftIndexCachePolicy.DirectoryScanFileNames, Is.EqualTo(new[] { ".git", "HEAD" }));
        Assert.That(MftIndexCachePolicy.CacheTag, Is.Not.EqualTo(default(CacheTag)));
        Assert.That(Path.GetDirectoryName(MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath)),
            Is.EqualTo(MftIndexCachePolicy.GetCacheRoot(files.DirectoryPath)));
    }

    [Test]
    public async Task DifferentCacheTagVersionCannotAdoptEarlierBlock()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        var path = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        var calls = 0;
        files.BeforeProduceAsync = (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        };
        FileIndexOptions Options(uint version, bool snapshot) => new()
        {
            Drives = [new IndexedDrive('X', files.TreePath, 1)],
            CacheDirectory = path,
            CacheTag = new CacheTag(SyntheticCacheTag.GetFourCharacterCode(MftIndexCachePolicy.CacheTag), version),
            CacheMode = snapshot ? CacheMode.ExistingOnly : CacheMode.Persistent,
            MftSource = SyntheticIndexSource.Create(files.Producer)
        };
        var version = SyntheticCacheTag.GetVersion(MftIndexCachePolicy.CacheTag);
        await using (var first = await FileIndex.OpenAsync(Options(1, false), CancellationToken.None))
            Assert.That(first.Drives.Single().Block?.Source, Is.EqualTo(BlockSource.ProducedByScan));
        await using (var declined = await FileIndex.OpenAsync(Options(version, true), CancellationToken.None))
            Assert.That(declined.Drives.Single().Failure?.Kind, Is.EqualTo(DriveFailureKind.CacheTagMismatch));
        Assert.That(calls, Is.EqualTo(1));
        await using (var second = await FileIndex.OpenAsync(Options(version, false), CancellationToken.None))
            Assert.That(second.Drives.Single().Block?.Source, Is.EqualTo(BlockSource.ProducedByScan));
        Assert.That(calls, Is.EqualTo(2));
        Assert.That(CacheDirectory.InspectCached(path).Single().CacheTag,
            Is.EqualTo(MftIndexCachePolicy.CacheTag));
    }
}
