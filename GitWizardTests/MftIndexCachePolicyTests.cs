using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;

namespace GitWizardTests;

[TestFixture]
public class MftIndexCachePolicyTests
{
    [Test]
    public void CanonicalPolicyIsStableAndNormalized()
    {
        using var files = new PackedIndexFixture();
        var current = MftIndexCachePolicy.Current;
        var reordered = new MftIndexCachePolicy(BrokerScanProfile.DirectoryIndex,
            ["HEAD", ".git", "HEAD"]);
        Assert.That(current.KeepFileNames, Is.EqualTo(new[] { ".git", "HEAD" }));
        Assert.That(current.Profile, Is.EqualTo(BrokerScanProfile.DirectoryIndex));
        Assert.That(current.GetCacheDirectory(files.DirectoryPath),
            Is.EqualTo(reordered.GetCacheDirectory(files.DirectoryPath)));
        Assert.That(Path.GetDirectoryName(current.GetCacheDirectory(files.DirectoryPath)),
            Is.EqualTo(MftIndexCachePolicy.GetCacheRoot(files.DirectoryPath)));
    }

    [TestCase("")]
    [TestCase("nested/HEAD")]
    [TestCase("nested\\HEAD")]
    public void RetentionEntriesMustBeFileNames(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            _ = new MftIndexCachePolicy(BrokerScanProfile.DirectoryIndex, [".git", name]));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task DifferentPolicyCannotAdoptEarlierBlock(bool changeProfile)
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        var original = MftIndexCachePolicy.Current;
        var changed = new MftIndexCachePolicy(
            changeProfile ? BrokerScanProfile.Full : BrokerScanProfile.DirectoryIndex,
            changeProfile ? original.KeepFileNames : [".git"]);
        var firstPath = original.GetCacheDirectory(files.DirectoryPath);
        var nextPath = changed.GetCacheDirectory(files.DirectoryPath);
        Assert.That(nextPath, Is.Not.EqualTo(firstPath));
        var calls = 0;
        MftBlockProducer produceMft = files.ProduceMftAsync;
        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken token)
        {
            calls++;
            return produceMft(request, token);
        }
        FileIndexOptions Options(string path, bool snapshot) => new()
        {
            Drives = [new IndexedDrive('X', files.TreePath, 1)],
            CacheDirectory = path,
            NoCache = false,
            InitialOpenCacheOnly = snapshot,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = Produce
        };
        await using (var first = await FileIndex.OpenAsync(Options(firstPath, false), CancellationToken.None))
            Assert.That(first.Drives.Single().BlockSource, Is.EqualTo(BlockSource.ProducedByScan));
        await using (var declined = await FileIndex.OpenAsync(Options(nextPath, true), CancellationToken.None))
            Assert.That(declined.Drives.Single().FailureKind, Is.EqualTo(DriveFailureKind.CacheDeclined));
        Assert.That(calls, Is.EqualTo(1));
        await using (var second = await FileIndex.OpenAsync(Options(nextPath, false), CancellationToken.None))
            Assert.That(second.Drives.Single().BlockSource, Is.EqualTo(BlockSource.ProducedByScan));
        Assert.That(calls, Is.EqualTo(2));
        await using var originalAgain = await FileIndex.OpenAsync(Options(firstPath, true), CancellationToken.None);
        Assert.That(originalAgain.Drives.Single().BlockSource, Is.EqualTo(BlockSource.WarmStartedFromCache));
        Assert.That(calls, Is.EqualTo(2));
    }
}
