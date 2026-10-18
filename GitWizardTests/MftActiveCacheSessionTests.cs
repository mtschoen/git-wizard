using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests;

public class MftActiveCacheSessionTests
{
    [Test]
    public async Task CaptureUsesIdentityAndDoesNotTransferOwner()
    {
        using var files = new PackedIndexFixture();
        var drives = new[] { new IndexedDrive('X', files.TreePath, 42, ProducerKind.Enumeration) };
        var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = drives,
            CacheDirectory = files.BlockDirectory
        }, CancellationToken.None);
        var session = new MftIndexSession(index, null, ["X"], drives,
            new Dictionary<string, string>(), files.BlockDirectory);
        var source = new IndexVolumeChangeSource(
            (_, _, _) => throw new AssertionException("Inventory opened a session"), session);
        var captured = source.CaptureCacheStatus();
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.CacheDirectory, Is.EqualTo(files.BlockDirectory));
        Assert.That(captured.Drives.Single().VolumeSerial, Is.EqualTo(42));
        Assert.That(captured.Drives.Single().Drive.IndexStatus!.Block?.CacheLocation,
            Is.EqualTo(CacheLocation.OwnedCanonical));
        Assert.That(index.Root('X').IsDirectory, Is.True);
        await source.DisposeAsync();
        Assert.That(source.CaptureCacheStatus(), Is.Null);
        Assert.That(session.CaptureCacheStatus(), Is.Null);
        Assert.That(captured.Drives.Single().Drive.IndexStatus!.Block?.CacheLocation,
            Is.EqualTo(CacheLocation.OwnedCanonical), "Previously captured values stay immutable");
    }
}
