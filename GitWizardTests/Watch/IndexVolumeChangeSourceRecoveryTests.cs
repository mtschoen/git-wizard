using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;
using static GitWizardTests.TestSupport.AsyncCondition;

namespace GitWizardTests.Watch;

/// <summary>
/// What a drive MFTLib recovered must look like to consumers: repositories created or deleted
/// while its watch was down reach the repository watch before the drive reads CaughtUp, and a
/// startup catch-up superseded by a fault never reports the drive caught up.
/// </summary>
[TestFixture]
public class IndexVolumeChangeSourceRecoveryTests
{
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    [Test]
    public async Task RecoveredDrive_ReconcilesRepositoriesChangedWhileItsWatchWasDown()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("oldrepo");
        files.AddMarker("keptrepo");
        var scripted = new ScriptedIndexWatchSource();
        var owner = IndexVolumeChangeSourceTests.Owner(await files.OpenMftIndexAsync(scripted, 'X', 'Y'), "X", "Y");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeProduceAsync = token => release.Task.WaitAsync(token);
        var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await using var sourceLifetime = source;
        var service = new RepositoryWatchService(source, [@"X:\oldrepo", @"X:\keptrepo"], [@"X:\"],
            TimeSpan.Zero, includeTempDirectories: true);
        using var cancellation = new CancellationTokenSource(HangGuard);
        var events = new List<RepositoryChangeEvent>();
        var run = ConsumeAsync(service, events, cancellation.Token);
        await WaitUntilAsync(() => source.Readiness.TryGet("X", out var status) && status.State == VolumeWatchState.CaughtUp);

        // Subscribed only now, so the first CaughtUp it sees is the recovered drive's.
        var recoveredCaughtUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.VolumeReadinessChanged += snapshot =>
        {
            if (snapshot.TryGet("X", out var status) && status.State == VolumeWatchState.CaughtUp)
                recoveredCaughtUp.TrySetResult();
        };
        scripted.FailDrive('X', new IOException("X failed"));
        await WaitUntilAsync(() => owner.Index.Drives.Single(drive => drive.DriveLetter == 'X').WatchCatchUp ==
                                   WatchCatchUpState.Recovering);
        Directory.Delete(Path.Combine(files.TreePath, "oldrepo"), recursive: true);
        files.AddMarker("newrepo");
        release.SetResult();

        await recoveredCaughtUp.Task.WaitAsync(HangGuard);
        await WaitUntilAsync(() =>
        {
            lock (events)
                return events.Count >= 3;
        });
        RepositoryChangeEvent[] observed;
        lock (events)
            observed = events.ToArray();
        Assert.That(observed, Does.Contain(new RepositoryChangeEvent(@"X:\newrepo", RepositoryChangeKind.Created)));
        Assert.That(observed, Does.Contain(new RepositoryChangeEvent(@"X:\oldrepo", RepositoryChangeKind.Deleted)));
        Assert.That(observed, Does.Contain(new RepositoryChangeEvent(@"X:\keptrepo", RepositoryChangeKind.Changed)),
            "A repository that survived the gap is refreshed, since its own changes were not observed.");

        await cancellation.CancelAsync();
        await run;
    }

    [Test]
    public async Task RecoveryReconciliation_IsDeliveredBeforeTheDriveReadsCaughtUp()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("oldrepo");
        var scripted = new ScriptedIndexWatchSource();
        var owner = IndexVolumeChangeSourceTests.Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeProduceAsync = token => release.Task.WaitAsync(token);
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        // Buffering makes delivery synchronous and observable at the moment readiness changes.
        source.StartStartupBuffering();
        var deliveredAtCaughtUp = new TaskCompletionSource<IReadOnlyList<VolumeChangeBatch>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Func<IReadOnlyList<VolumeChangeBatch>> drainBuffer = source.CompleteStartupBuffer;
        // Subscribed after the arm, so the first CaughtUp it sees is the recovered drive's.
        source.ReadinessChanged += snapshot =>
        {
            if (snapshot.TryGet("X", out var status) && status.State == VolumeWatchState.CaughtUp)
                deliveredAtCaughtUp.TrySetResult(drainBuffer());
        };

        scripted.FailDrive('X', new IOException("X failed"));
        await WaitUntilAsync(() => owner.Index.Drives.Single().WatchCatchUp == WatchCatchUpState.Recovering);
        Directory.Delete(Path.Combine(files.TreePath, "oldrepo"), recursive: true);
        files.AddMarker("newrepo");
        release.SetResult();

        var delivered = await deliveredAtCaughtUp.Task.WaitAsync(HangGuard);
        Assert.That(delivered.Select(batch => batch.Volume), Is.All.EqualTo("X"));
        Assert.That(delivered.SelectMany(batch => batch.Entries), Is.EquivalentTo(new[]
        {
            new VolumeChangeEntry(@"X:\oldrepo\.git", VolumeEntryKind.Deleted),
            new VolumeChangeEntry(@"X:\newrepo\.git", VolumeEntryKind.Created)
        }), "The reconciliation is delivered before the drive reads CaughtUp.");
    }

    [Test]
    public async Task LiveDeletionAppliedAfterTheReconciliationRead_IsDeliveredAfterIt()
    {
        using var files = new PackedIndexFixture();
        var scripted = new ScriptedIndexWatchSource();
        var owner = IndexVolumeChangeSourceTests.Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeProduceAsync = token => release.Task.WaitAsync(token);
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        source.ReconciliationCapturedForTest = _ =>
        {
            // The recovered block has newrepo; the journal now deletes it. MFTLib applies the
            // batch to the block before it raises Changed, so waiting for the block here
            // means the deletion's delivery is racing the reconciliation.
            var marker = owner.Index.FindByName(".git").Single();
            scripted.Publish('X', new JournalBatch(
                [IndexVolumeChangeSourceTests.Entry(marker.Id.RecordNumber, marker.Parent!.Value.Id.RecordNumber,
                    ".git", UsnReason.FileDelete, directory: true)],
                PackedIndexFixture.JournalId, 1003));
            Assert.That(SpinWait.SpinUntil(() => owner.Index.FindByName(".git").Count == 0, HangGuard), Is.True);
        };
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        using var cancellation = new CancellationTokenSource(HangGuard);
        await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        scripted.FailDrive('X', new IOException("X failed"));
        await WaitUntilAsync(() => owner.Index.Drives.Single().WatchCatchUp == WatchCatchUpState.Recovering);
        files.AddMarker("newrepo");
        release.SetResult();

        Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(HangGuard), Is.True);
        Assert.That(reader.Current.Entries, Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"X:\newrepo\.git", VolumeEntryKind.Created)
        }), "The reconciliation read the block before the deletion, so it is delivered first.");
        Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(HangGuard), Is.True);
        Assert.That(reader.Current.Entries, Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"X:\newrepo\.git", VolumeEntryKind.Deleted)
        }));
    }

    [Test]
    public async Task StartupCatchUpSupersededByAFault_FollowsTheRecoveryInsteadOfReportingCaughtUp()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedIndexWatchSource();
        var owner = IndexVolumeChangeSourceTests.Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rescanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeProduceAsync = token =>
        {
            rescanStarted.TrySetResult();
            return release.Task.WaitAsync(token);
        };
        var faultedAfterCatchUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner))
        {
            StartupCatchUpSettledForTest = async drive =>
            {
                // The drive faults after its first catch-up settled and before the arm reports it.
                scripted.FailDrive(drive, new IOException("X failed after catching up"));
                await WaitUntilAsync(() => owner.Index.Drives.Single().WatchCatchUp == WatchCatchUpState.Recovering);
                faultedAfterCatchUp.SetResult();
            }
        };

        var arming = source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        await faultedAfterCatchUp.Task.WaitAsync(HangGuard);
        // The recovery rescan has begun and the source has published the drive as catching up, so
        // the arm has had its chance to wrongly report the superseded catch-up.
        await rescanStarted.Task.WaitAsync(HangGuard);
        await WaitUntilAsync(() => source.Readiness.TryGet("X", out var published) &&
                                   published.State == VolumeWatchState.CatchingUp);

        Assert.That(arming.IsCompleted, Is.False, "The arm follows the recovery the fault started.");
        Assert.That(source.Readiness.TryGet("X", out var recovering), Is.True);
        Assert.That(recovering!.State, Is.EqualTo(VolumeWatchState.CatchingUp));

        var releaseIssued = new TaskCompletionSource();
        var completedBeforeRelease = arming.ContinueWith(_ => !releaseIssued.Task.IsCompleted,
            TaskScheduler.Default);
        releaseIssued.SetResult();
        release.SetResult();
        var result = await arming.WaitAsync(HangGuard);
        Assert.That(await completedBeforeRelease.WaitAsync(HangGuard), Is.False,
            "The arm must not complete before the recovery finishes.");
        Assert.That(result.Errors, Is.Empty);
        Assert.That(owner.Index.Drives.Single().WatchCatchUp, Is.EqualTo(WatchCatchUpState.CaughtUp));
        Assert.That(source.Readiness.TryGet("X", out var recovered), Is.True);
        Assert.That(recovered!.State, Is.EqualTo(VolumeWatchState.CaughtUp));
    }

    static async Task ConsumeAsync(RepositoryWatchService service, List<RepositoryChangeEvent> events,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var change in service.RunAsync(cancellationToken))
            {
                lock (events)
                    events.Add(change);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The test ends the run by cancelling it.
        }
    }
}
