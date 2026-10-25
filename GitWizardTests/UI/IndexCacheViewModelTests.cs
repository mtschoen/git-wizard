using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.UI;

// Covers IndexCacheViewModel.cs, MainViewModel.cs, and PreparedDiscoveryIndexStore.cs.
public class IndexCacheViewModelTests
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
        var model = new IndexCacheViewModel(new StubUiDispatcher(), new StubUserDialogs(), new PreparedDiscoveryIndexStore(), () => attempts.Dequeue()(), NoClear);
        await model.RefreshAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.Text, Does.Contain("inspection failed"));
            Assert.That(model.Snapshot, Is.Null);
            Assert.That(model.IsIdle, Is.True);
        }
        await model.RefreshAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.Snapshot, Is.SameAs(snapshot));
            Assert.That(model.Text, Does.Contain("No cached blocks"));
        }
    }

    [Test]
    public async Task SecondRefreshDoesNotStartAnotherInspection()
    {
        using var files = new PackedIndexFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new MftIndexCacheInspection(new FrozenCacheClock(), true,
            MftIndexCacheInspection.InspectDirectory).Capture(files.DirectoryPath);
        var model = new IndexCacheViewModel(new StubUiDispatcher(), new StubUserDialogs(), new PreparedDiscoveryIndexStore(), () =>
        {
            entered.TrySetResult();
            return !release.Task.Wait(TimeSpan.FromSeconds(5)) ? throw new TimeoutException("Test did not release inspection") : snapshot;
        }, NoClear);
        var first = model.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(model.IsIdle, Is.False);
            await model.RefreshAsync();
            Assert.That(first.IsCompleted, Is.False);
        }
        finally
        {
            release.TrySetResult();
            await first;
        }
        Assert.That(model.IsIdle, Is.True);
    }

    [Test]
    public async Task ClearAndRefreshShareOneGuard()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clears = 0;
        var inspections = 0;
        var model = new IndexCacheViewModel(new StubUiDispatcher(), new StubUserDialogs(), new PreparedDiscoveryIndexStore(),
            () =>
            {
                Interlocked.Increment(ref inspections);
                throw new AssertionException("Refresh must not inspect while clearing");
            },
            () =>
            {
                Interlocked.Increment(ref clears);
                entered.TrySetResult();
                return !release.Task.Wait(TimeSpan.FromSeconds(5)) ? throw new TimeoutException("Test did not release clear") : NoClear();
            });
        var first = model.ClearAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(model.IsIdle, Is.False);
            await model.RefreshAsync();
            Assert.That(inspections, Is.Zero,
                "Refresh must not enter inspection while Clear holds the guard");
            await model.ClearAsync();
            Assert.That(clears, Is.EqualTo(1));
        }
        finally
        {
            release.TrySetResult();
            await first;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.IsIdle, Is.True);
            Assert.That(model.Text, Does.StartWith("Index cache clear completed."));
        }
    }

    [TestCase(CachedBlockAvailability.Available)]
    [TestCase(CachedBlockAvailability.Invalid)]
    [TestCase(CachedBlockAvailability.InUse)]
    public async Task ModelPublishesInventoryOutcomes(CachedBlockAvailability availability)
    {
        using var files = new PackedIndexFixture();
        var row = SyntheticNotifications.CreateCachedBlockStatus(SyntheticNotifications.CreateCachedBlockFile('X', 1, "fixture", 42,
            FrozenCacheClock.Now.UtcDateTime.AddMinutes(-5)), availability, null, null, null);
        var snapshot = new MftIndexCacheInspection(new FrozenCacheClock(), true,
            _ => [row]).Capture(files.DirectoryPath);
        var model = new IndexCacheViewModel(new StubUiDispatcher(), new StubUserDialogs(), new PreparedDiscoveryIndexStore(), () => snapshot, NoClear);
        await model.RefreshAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.Snapshot!.Inventory.Single().Availability, Is.EqualTo(availability));
            Assert.That(model.Text, Does.Contain("age: 5m"));
        }
        Assert.That(model.Text, Does.Contain("serial 00000001"));
    }

    [Test]
    public async Task PreparedOwnerCaptureFollowsTransferAndDisposal()
    {
        using var files = new PackedIndexFixture();
        var drives = new[] { new IndexedDrive('X', files.TreePath, 1, ProducerKind.Enumeration) };
        var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = drives,
            CacheDirectory = files.BlockDirectory
        }, CancellationToken.None);
        var session = new MftIndexSession(index, null, ["X"], drives,
            new Dictionary<string, string>(), files.BlockDirectory);
        await using var main = TestViewModels.Create();
        await main.PreparedDiscoveryIndex.SetAsync(session);
        Assert.That(main.PreparedDiscoveryIndex.CaptureActiveCacheSessions(liveSource: null).Single().Drives.Single().Drive.IndexStatus!.Block?.CacheLocation,
            Is.EqualTo(CacheLocation.OwnedCanonical));
        var source = new IndexVolumeChangeSource(
            (_, _, _) => throw new AssertionException("Inspection must not open"), main.PreparedDiscoveryIndex.Take());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(main.PreparedDiscoveryIndex.Current, Is.Null);
            Assert.That(source.CaptureCacheStatus()!.Drives.Single().VolumeSerial, Is.EqualTo(1));
            Assert.That(main.PreparedDiscoveryIndex.CaptureActiveCacheSessions(source)
                .Single().Drives.Single().VolumeSerial, Is.EqualTo(1));
        }

        using var secondFiles = new PackedIndexFixture();
        var secondDrives = new[] { new IndexedDrive('Y', secondFiles.TreePath, 2, ProducerKind.Enumeration) };
        var secondIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = secondDrives,
            CacheDirectory = secondFiles.BlockDirectory
        }, CancellationToken.None);
        var secondSession = new MftIndexSession(secondIndex, null, ["Y"], secondDrives,
            new Dictionary<string, string>(), secondFiles.BlockDirectory);
        await main.PreparedDiscoveryIndex.SetAsync(secondSession);
        var captures = main.PreparedDiscoveryIndex.CaptureActiveCacheSessions(source);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(captures.Select(capture => capture.Drives.Single().VolumeSerial),
                    Is.EqualTo(new uint?[] { 1, 2 }));
            Assert.That(main.PreparedDiscoveryIndex.Current, Is.SameAs(secondSession));
            Assert.That(secondSession.IsDisposed, Is.False);
        }
        await main.PreparedDiscoveryIndex.SetAsync(null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondSession.IsDisposed, Is.True);
            Assert.That(main.PreparedDiscoveryIndex.CaptureActiveCacheSessions(source), Has.Count.EqualTo(1));
        }
        await source.DisposeAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.CaptureCacheStatus(), Is.Null);
            Assert.That(main.PreparedDiscoveryIndex.CaptureActiveCacheSessions(source), Is.Empty);
            Assert.That(main.PreparedDiscoveryIndex.CaptureActiveCacheSessions(liveSource: null), Is.Empty);
        }
    }

    static MftIndexCacheMaintenanceResult NoClear() => new([], [], []);
}
