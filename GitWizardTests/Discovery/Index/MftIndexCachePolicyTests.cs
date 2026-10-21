using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests;

[TestFixture]
public class MftIndexCachePolicyTests
{
    [Test]
    public void PolicyDefinesTheDiscoveryScanAndOneCacheDirectory()
    {
        using var files = new PackedIndexFixture();
        Assert.That(MftIndexCachePolicy.DirectoryScanFileNames, Is.EqualTo(new[] { ".git", "HEAD" }));
        Assert.That(SyntheticCacheTag.GetFourCharacterCode(MftIndexCachePolicy.CacheTag), Is.EqualTo("GWMI"));
        Assert.That(SyntheticCacheTag.GetVersion(MftIndexCachePolicy.CacheTag), Is.EqualTo(2u));
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
            Assert.That(first.Drives.Single().Block?.Origin, Is.EqualTo(BlockOrigin.ProducedByScan));
        await using (var declined = await FileIndex.OpenAsync(Options(version, true), CancellationToken.None))
            Assert.That(declined.Drives.Single().Failure?.Kind, Is.EqualTo(DriveFailureKind.CacheIdentityMismatch));
        Assert.That(calls, Is.EqualTo(1));
        await using (var second = await FileIndex.OpenAsync(Options(version, false), CancellationToken.None))
            Assert.That(second.Drives.Single().Block?.Origin, Is.EqualTo(BlockOrigin.ProducedByScan));
        Assert.That(calls, Is.EqualTo(2));
        Assert.That(CacheDirectory.InspectCached(path).Single().CacheTag,
            Is.EqualTo(MftIndexCachePolicy.CacheTag));
    }
}
