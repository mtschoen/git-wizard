using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;
using MFTLib.Index;

namespace GitWizardTests.UI;

public class IndexCacheStatusViewModelTests
{
    [Test]
    public async Task RefreshSurfacesFailuresAndRecovers()
    {
        using var files = new PackedIndexFixture();
        var snapshot = new MftIndexCacheInspection(new FrozenCacheClock(), true,
            MftIndexCacheInspection.InspectDirectory).Capture(files.DirectoryPath);
        var attempts = new Queue<Func<MftIndexCacheSnapshot>>([
            () => throw new IOException("inspection failed"),
            () => snapshot
        ]);
        var model = new IndexCacheStatusViewModel(new StubUiDispatcher(), () => attempts.Dequeue()());
        await model.RefreshAsync();
        Assert.That(model.Text, Does.Contain("inspection failed"));
        Assert.That(model.Snapshot, Is.Null);
        Assert.That(model.CanRefresh, Is.True);
        await model.RefreshAsync();
        Assert.That(model.Snapshot, Is.SameAs(snapshot));
        Assert.That(model.Text, Does.Contain("No cached blocks"));
    }

    [Test]
    public async Task SecondRefreshDoesNotStartAnotherInspection()
    {
        using var files = new PackedIndexFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new MftIndexCacheInspection(new FrozenCacheClock(), true,
            MftIndexCacheInspection.InspectDirectory).Capture(files.DirectoryPath);
        var model = new IndexCacheStatusViewModel(new StubUiDispatcher(), () =>
        {
            entered.TrySetResult();
            if (!release.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Test did not release inspection");
            return snapshot;
        });
        var first = model.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(model.CanRefresh, Is.False);
            await model.RefreshAsync();
            Assert.That(first.IsCompleted, Is.False);
        }
        finally
        {
            release.TrySetResult();
            await first;
        }
        Assert.That(model.CanRefresh, Is.True);
    }

    [TestCase(CachedBlockAvailability.Available)]
    [TestCase(CachedBlockAvailability.Invalid)]
    [TestCase(CachedBlockAvailability.InUse)]
    public async Task ModelPublishesInventoryOutcomes(CachedBlockAvailability availability)
    {
        using var files = new PackedIndexFixture();
        var row = new CachedBlockStatus(new CachedBlockFile('X', 1, "fixture", 42,
            FrozenCacheClock.Now.UtcDateTime.AddMinutes(-5)), availability, null, null, null);
        var snapshot = new MftIndexCacheInspection(new FrozenCacheClock(), true,
            _ => new[] { row }).Capture(files.DirectoryPath);
        var model = new IndexCacheStatusViewModel(new StubUiDispatcher(), () => snapshot);
        await model.RefreshAsync();
        Assert.That(model.Snapshot!.Inventory.Single().Availability, Is.EqualTo(availability));
        Assert.That(model.Text, Does.Contain("age: 5m"));
        Assert.That(model.Text, Does.Contain("serial 00000001"));
    }

    [Test]
    public async Task PreparedOwnerCaptureFollowsTransferAndDisposal()
    {
        using var files = new PackedIndexFixture();
        var drives = new[] { new IndexedDrive('X', files.TreePath, 1) };
        var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = drives,
            CacheDirectory = files.BlockDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        }, CancellationToken.None);
        var session = new MftIndexSession(index, null, ["X"], drives,
            new Dictionary<string, string>(), files.BlockDirectory);
        await using var main = new MainViewModel(new StubUiDispatcher(),
            new StubUserDialogs(), new StubClipboardService());
        await main.SetPreparedDiscoveryIndexAsync(session);
        Assert.That(main.CaptureActiveCacheSessions().Single().Drives.Single().Drive.IndexStatus!.CacheSlot,
            Is.EqualTo(CacheSlotState.OwnedCanonical));
        var source = new IndexVolumeChangeSource(
            (_, _, _) => throw new AssertionException("Inspection must not open"), main.TakePreparedDiscoveryIndex());
        Assert.That(main.PreparedDiscoveryIndex, Is.Null);
        Assert.That(source.CaptureCacheStatus()!.Drives.Single().VolumeSerial, Is.EqualTo(1));
        await source.DisposeAsync();
        Assert.That(source.CaptureCacheStatus(), Is.Null);
        Assert.That(main.CaptureActiveCacheSessions(), Is.Empty);
    }
}
