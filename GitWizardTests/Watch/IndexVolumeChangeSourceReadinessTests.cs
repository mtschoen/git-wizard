using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;
using MFTLibTestExtensions;
using static GitWizardTests.TestSupport.AsyncCondition;

namespace GitWizardTests.Watch;

[TestFixture]
public class IndexVolumeChangeSourceReadinessTests
{

    static MftIndexSession Owner(FileIndex index, params string[] drives) =>
        new(index, null, drives, [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    [Test]
    public async Task ReadinessFollowsPerDriveCatchUpMarkers()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource();
        var owner = Owner(await files.OpenMftIndexAsync(scripted, 'X', 'Y'), "X", "Y");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        var snapshots = new List<VolumeReadinessSnapshot>();
        source.ReadinessChanged += snapshots.Add;
        Assert.That(source.Mode, Is.EqualTo(WatchReadinessMode.JournalBacklog));
        Assert.That(source.Readiness.Volumes, Is.Empty);

        var arming = source.ArmAndCatchUpAsync(["X", "Y"], CancellationToken.None);
        await Task.WhenAll(scripted.WaitForStartAsync('X', 1, CancellationToken.None).WaitAsync(TestTimeouts.HangGuard),
            scripted.WaitForStartAsync('Y', 1, CancellationToken.None).WaitAsync(TestTimeouts.HangGuard));
        await WaitUntilAsync(() => source.Readiness.Volumes.Count == 2);
        Assert.That(arming.IsCompleted, Is.False, "The arm must wait for catch-up, not merely for pump start.");
        Assert.That(source.Readiness.Volumes.Select(v => (v.Volume, v.State, v.Origin)), Is.EquivalentTo(new[]
        {
            ("X", VolumeWatchState.CatchingUp, VolumeBlockOrigin.Cold),
            ("Y", VolumeWatchState.CatchingUp, VolumeBlockOrigin.Cold)
        }));
        Assert.That(source.Readiness.IsSettled, Is.False);

        await scripted.WatchFor('X').PublishCaughtUpAsync();
        await WaitUntilAsync(() => source.Readiness.TryGet("X", out var x) && x.State == VolumeWatchState.CaughtUp);
        Assert.That(arming.IsCompleted, Is.False, "One caught-up drive does not settle a two-drive arm.");
        Assert.That(source.Readiness.HasCaughtUpVolume, Is.True);
        Assert.That(source.Readiness.IsSettled, Is.False);

        scripted.WatchFor('Y').LoseChannel(new IOException("Y failed"));
        var result = await arming.WaitAsync(TestTimeouts.HangGuard);
        Assert.That(result.Errors["Y"], Is.EqualTo("Y failed"));
        Assert.That(source.Readiness.IsSettled, Is.True);
        Assert.That(source.Readiness.CaughtUpVolumes, Is.EqualTo(new[] { "X" }));
        var excluded = source.Readiness.ExcludedVolumes.Single();
        Assert.That((excluded.Volume, excluded.ExclusionReason), Is.EqualTo(("Y", "Y failed")));
        Assert.That(snapshots.Last(), Is.SameAs(source.Readiness));
        Assert.That(snapshots.Select(s => s.Volumes.Count), Is.All.LessThanOrEqualTo(2));
    }

    [Test]
    public async Task ImmediateCatchUpSettlesBeforeArmReturnsAndLateSubscriberReadsSnapshot()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var lossPending = 1;
        var retained = SyntheticCheckpointLoss.WithJournalPositions(
            SyntheticNotifications.CreateJournalCheckpointLoss('X', JournalCheckpointLossCause.CheckpointTrimmed,
                JournalCheckpointLossDetection.ScanCatchUp,
                new UsnJournalSettings { MaximumSize = 1024, AllocationDelta = 64 }), 1000, 2000, 3000);
        files.CatchUpLoss = _ => Interlocked.Exchange(ref lossPending, 0) == 1 ? retained : null;
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = new MftIndexSession(await files.OpenMftIndexAsync(scripted, 'X'), null, ["X"],
            [new IndexedDrive('X', files.TreePath, 12345u)], new Dictionary<string, string>());
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        var result = await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        // The returned retained-loss hint describes the drive after catch-up, not before watching started.
        var warning = result.JournalWarnings.Single();
        Assert.Multiple(() =>
        {
            Assert.That(warning.Loss, Is.SameAs(retained));
            Assert.That(warning.WatchState, Is.EqualTo(WatchState.CaughtUp));
            Assert.That(warning.Message, Does.Contain("was rescanned and the watch has caught up"));
        });
        // No subscriber was attached during the arm: the settled state must be readable afterwards.
        var snapshot = source.Readiness;
        Assert.That(snapshot.IsSettled, Is.True);
        Assert.That(snapshot.Volumes.Single().State, Is.EqualTo(VolumeWatchState.CaughtUp));
        Assert.That(snapshot.Volumes.Single().Origin, Is.EqualTo(VolumeBlockOrigin.Cold));
    }

    [Test]
    public async Task OpenFailedDriveIsExcludedWithItsProducerMessage()
    {
        using var files = new PackedIndexFixture();
        await using var failedIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('X', files.TreePath, 1)],
            CacheMode = CacheMode.Disabled,
            MftSource = SyntheticIndexSource.Create(new SyntheticMftProducer(_ => throw new IOException("cannot scan X")))
        }, CancellationToken.None);
        var owner = Owner(failedIndex, "X");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        var status = source.Readiness.Volumes.Single();
        Assert.That(status.State, Is.EqualTo(VolumeWatchState.Excluded));
        Assert.That(status.ExclusionReason, Is.EqualTo("cannot scan X"));
        Assert.That(status.Origin, Is.EqualTo(VolumeBlockOrigin.Unknown));
        Assert.That(source.Readiness.IsSettled, Is.True);
        Assert.That(source.Readiness.HasCaughtUpVolume, Is.False);
    }

    [Test]
    public async Task EnumerationBackedDriveIsExcludedNotCaughtUp()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var owner = Owner(await files.OpenIndexAsync('X'), "X");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        var status = source.Readiness.Volumes.Single();
        Assert.That(status.State, Is.EqualTo(VolumeWatchState.Excluded));
        Assert.That(status.ExclusionReason, Is.EqualTo("Live watch is not supported for this drive's index."));
    }

    [Test]
    public async Task LaterWatchFaultMovesDriveToExcludedAfterSettling()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = Owner(await files.OpenMftIndexAsync(scripted, 'X', 'Y'), "X", "Y");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X", "Y"], CancellationToken.None);
        Assert.That(source.Readiness.CaughtUpVolumes, Is.EquivalentTo(new[] { "X", "Y" }));
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        scripted.WatchFor('Y').LoseChannel(new IOException("Y died later"));
        await WaitUntilAsync(() => source.Readiness.TryGet("Y", out var y) && y.State == VolumeWatchState.Excluded);
        Assert.That(source.Readiness.TryGet("Y", out var status) ? status.ExclusionReason : null, Is.EqualTo("Y died later"));
        Assert.That(source.Readiness.CaughtUpVolumes, Is.EqualTo(new[] { "X" }));
        await cancellation.CancelAsync();
        try { await pending; } catch (OperationCanceledException) { }
    }

    [Test]
    public async Task ReadinessNotificationsAreSerializedInTransitionOrder()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource();
        var owner = Owner(await files.OpenMftIndexAsync(scripted, 'X', 'Y'), "X", "Y");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        var snapshots = new List<VolumeReadinessSnapshot>();
        var lockObj = new object();
        source.ReadinessChanged += s =>
        {
            lock (lockObj)
                snapshots.Add(s);
        };
        var arming = source.ArmAndCatchUpAsync(["X", "Y"], CancellationToken.None);
        await Task.WhenAll(scripted.WaitForStartAsync('X', 1, CancellationToken.None).WaitAsync(TestTimeouts.HangGuard),
            scripted.WaitForStartAsync('Y', 1, CancellationToken.None).WaitAsync(TestTimeouts.HangGuard));
        await WaitUntilAsync(() => snapshots.Count >= 2);

        await Task.WhenAll(
            scripted.WatchFor('X').PublishCaughtUpAsync(),
            scripted.WatchFor('Y').PublishCaughtUpAsync());

        await arming.WaitAsync(TestTimeouts.HangGuard);
        Assert.That(snapshots.Last().IsSettled, Is.True);
        Assert.That(snapshots.Last().CaughtUpVolumes, Is.EquivalentTo(new[] { "X", "Y" }));
        Assert.That(snapshots.Last(), Is.EqualTo(source.Readiness));
    }
}
