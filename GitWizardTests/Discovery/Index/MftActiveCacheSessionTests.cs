using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests;

public class MftActiveCacheSessionTests
{
    [TestCase(CacheMode.Persistent, CacheLocation.OwnedCanonical)]
    [TestCase(CacheMode.Disabled, null)]
    public async Task CaptureUsesLiveSerialAndDoesNotTransferOwner(CacheMode cacheMode, CacheLocation? cacheLocation)
    {
        using var files = new PackedIndexFixture();
        var drive = (RootKey)new VolumeKey('X');
        var index = await FileIndex.OpenAsync(SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = drive,
            CacheMode = cacheMode,
            CacheDirectory = files.BlockDirectory
        } with
        { Producer = ProducerSelection.WalkOnly }, 42, volume: new VolumeKey('X'), volumeRootDirectory: files.TreePath), CancellationToken.None);
        var session = new MftIndexSession([index], null, ["X"], new Dictionary<string, string>(), files.BlockDirectory);
        var source = new IndexVolumeChangeSource(
            (_, _, _) => throw new AssertionException("Inventory opened a session"), session);
        var captured = source.CaptureCacheStatus();
        Assert.That(captured, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured!.CacheDirectory, Is.EqualTo(files.BlockDirectory));
            Assert.That(captured.Drives.Single().VolumeSerial, Is.EqualTo(42));
            Assert.That(captured.Drives.Single().Drive.IndexStatus!.Block?.CacheLocation,
                Is.EqualTo(cacheLocation));
            Assert.That(index.Root().IsDirectory, Is.True);
        }
        await source.DisposeAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.CaptureCacheStatus(), Is.Null);
            Assert.That(session.CaptureCacheStatus(), Is.Null);
            Assert.That(captured.Drives.Single().Drive.IndexStatus!.Block?.CacheLocation,
                Is.EqualTo(cacheLocation), "Previously captured values stay immutable");
        }
    }
}
