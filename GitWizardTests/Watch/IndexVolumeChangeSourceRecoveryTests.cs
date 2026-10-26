using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;
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

    [Test]
    public async Task RecoveredDrive_ReconcilesRepositoriesChangedWhileItsWatchWasDown()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("oldrepo");
        files.AddMarker("keptrepo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = PackedIndexFixture.Owner(await files.OpenMftIndexAsync(scripted, 'X', 'Y'), "X", "Y");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeProduceAsync = (_, token) => release.Task.WaitAsync(token);
        var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await using var sourceLifetime = source;
        var service = new RepositoryWatchService(source, [@"X:\oldrepo", @"X:\keptrepo"], [@"X:\"],
            TimeSpan.Zero, includeTempDirectories: true);
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        var events = new List<RepositoryChangeEvent>();
        var run = ConsumeAsync(service, events, cancellation.Token);
        await WaitUntilAsync(() => source.Readiness.TryGet("X", out var status) && status.State == VolumeWatchState.CaughtUp);

        // Subscribed only now, so the first CaughtUp it sees is the recovered drive's.
        var recoveredCaughtUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.VolumeReadinessChanged += snapshot =>
        {
            if (snapshot.TryGet("X", out var status) && status.State == VolumeWatchState.CaughtUp)
            {
                recoveredCaughtUp.TrySetResult();
            }
        };
        scripted.WatchFor('X').FailDrive(new IOException("X failed"));
        await WaitUntilAsync(() => owner.Index.Drives.Single(drive => drive.Drive == 'X').Watch.State ==
                                   WatchState.Recovering);
        Directory.Delete(Path.Combine(files.TreePath, "oldrepo"), recursive: true);
        files.AddMarker("newrepo");
        release.SetResult();

        await recoveredCaughtUp.Task.WaitAsync(TestTimeouts.HangGuard);
        await WaitUntilAsync(() =>
        {
            lock (events)
            {
                return events.Count >= 3;
            }
        });
        RepositoryChangeEvent[] observed;
        lock (events)
        {
            observed = [.. events];
        }

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
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = PackedIndexFixture.Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeProduceAsync = (_, token) => release.Task.WaitAsync(token);
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        // Buffering makes delivery synchronous and observable at the moment readiness changes.
        source.StartStartupBuffering();
        var deliveredAtCaughtUp = new TaskCompletionSource<IReadOnlyList<VolumeChangeBatch>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var drainBuffer = source.CompleteStartupBuffer;
        // Subscribed after the arm, so the first CaughtUp it sees is the recovered drive's.
        source.ReadinessChanged += snapshot =>
        {
            if (snapshot.TryGet("X", out var status) && status.State == VolumeWatchState.CaughtUp)
            {
                deliveredAtCaughtUp.TrySetResult(drainBuffer());
            }
        };

        scripted.WatchFor('X').FailDrive(new IOException("X failed"));
        await WaitUntilAsync(() => owner.Index.Drives.Single().Watch.State == WatchState.Recovering);
        Directory.Delete(Path.Combine(files.TreePath, "oldrepo"), recursive: true);
        files.AddMarker("newrepo");
        release.SetResult();

        var delivered = await deliveredAtCaughtUp.Task.WaitAsync(TestTimeouts.HangGuard);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(delivered.Select(batch => batch.Volume), Is.All.EqualTo("X"));
            Assert.That(delivered.SelectMany(batch => batch.Entries), Is.EquivalentTo([
                new VolumeChangeEntry(@"X:\oldrepo\.git", VolumeEntryKind.Deleted),
                new VolumeChangeEntry(@"X:\newrepo\.git", VolumeEntryKind.Created)
            ]), "The reconciliation is delivered before the drive reads CaughtUp.");
        }
    }

    [Test]
    public async Task LiveDeletionAppliedAfterTheReconciliationRead_IsDeliveredAfterIt()
    {
        using var files = new PackedIndexFixture();
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = PackedIndexFixture.Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeProduceAsync = (_, token) => release.Task.WaitAsync(token);
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        source.ReconciliationCapturedForTest = __ =>
        {
            // The recovered block has newrepo; the journal now deletes it. MFTLib applies the
            // batch to the block before it raises Changed, so waiting for the block here
            // means the deletion's delivery is racing the reconciliation.
            var marker = owner.Index.Search(new SearchQuery(".git", NameMatchMode.Exact)).Single();
            _ = scripted.WatchFor('X').QueueBatchAsync([PackedIndexFixture.Entry(marker.RecordKey.RecordNumber, marker.Parent!.Value.RecordKey.RecordNumber,
                    ".git", SyntheticJournalReason.FileDelete, directory: true)], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1003));
            Assert.That(SpinWait.SpinUntil(() => owner.Index.Search(new SearchQuery(".git", NameMatchMode.Exact)).Count == 0, TestTimeouts.HangGuard), Is.True);
        };
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        scripted.WatchFor('X').FailDrive(new IOException("X failed"));
        await WaitUntilAsync(() => owner.Index.Drives.Single().Watch.State == WatchState.Recovering);
        files.AddMarker("newrepo");
        release.SetResult();

        var reconciled = await reader.MoveNextAsync().AsTask().WaitAsync(TestTimeouts.HangGuard);
        Assert.That(reconciled, Is.True);
        var reconciliationEntries = reader.Current.Entries;
        Assert.That(reconciliationEntries, Is.EqualTo([
            new VolumeChangeEntry(@"X:\newrepo\.git", VolumeEntryKind.Created)
        ]), "The reconciliation read the block before the deletion, so it is delivered first.");

        var deleted = await reader.MoveNextAsync().AsTask().WaitAsync(TestTimeouts.HangGuard);
        Assert.That(deleted, Is.True);
        var deletionEntries = reader.Current.Entries;
        Assert.That(deletionEntries, Is.EqualTo([
            new VolumeChangeEntry(@"X:\newrepo\.git", VolumeEntryKind.Deleted)
        ]));
    }

    [Test]
    public async Task StartupCatchUpSupersededByAFault_FollowsTheRecoveryInsteadOfReportingCaughtUp()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = PackedIndexFixture.Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rescanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeProduceAsync = (_, token) =>
        {
            rescanStarted.TrySetResult();
            return release.Task.WaitAsync(token);
        };
        var sourceUnderTest = new TaskCompletionSource<IndexVolumeChangeSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var faultedAfterCatchUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner))
        {
            StartupCatchUpSettledForTest = StartupCatchUpSettled
        };

        sourceUnderTest.SetResult(source);
        var arming = source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        await faultedAfterCatchUp.Task.WaitAsync(TestTimeouts.HangGuard);
        // The recovery rescan has begun and the source has published the drive as catching up, so
        // the arm has had its chance to wrongly report the superseded catch-up.
        await rescanStarted.Task.WaitAsync(TestTimeouts.HangGuard);
        await WaitUntilAsync(() => source.Readiness.TryGet("X", out var published) &&
                                   published.State == VolumeWatchState.CatchingUp);

        Assert.That(arming.IsCompleted, Is.False, "The arm follows the recovery the fault started.");
        var foundRecovering = source.Readiness.TryGet("X", out var recovering);
        Assert.That(foundRecovering, Is.True);
        var recoveringState = recovering!.State;
        Assert.That(recoveringState, Is.EqualTo(VolumeWatchState.CatchingUp));

        var releaseIssued = new TaskCompletionSource();
        var completedBeforeRelease = arming.ContinueWith(_ => !releaseIssued.Task.IsCompleted,
            TaskScheduler.Default);
        releaseIssued.SetResult();
        release.SetResult();
        var result = await arming.WaitAsync(TestTimeouts.HangGuard);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await completedBeforeRelease.WaitAsync(TestTimeouts.HangGuard), Is.False,
                    "The arm must not complete before the recovery finishes.");
            Assert.That(result.Errors, Is.Empty);
            Assert.That(owner.Index.Drives.Single().Watch.State, Is.EqualTo(WatchState.CaughtUp));
        }

        var foundRecovered = source.Readiness.TryGet("X", out var recovered);
        Assert.That(foundRecovered, Is.True);
        var recoveredState = recovered!.State;
        Assert.That(recoveredState, Is.EqualTo(VolumeWatchState.CaughtUp));
        return;

        async Task StartupCatchUpSettled(char drive)
        {
            // The drive faults after its first catch-up settled and before the arm reports it.
            scripted.WatchFor(drive).FailDrive(new IOException("X failed after catching up"));
            await WaitUntilAsync(() => owner.Index.Drives.Single().Watch.State == WatchState.Recovering);
            // The index reports Recovering before it delivers the state to the source, so the arm
            // could still read the readiness of the superseded catch-up. Hold the arm until the
            // source has published the recovery, which is what this test asserts it follows.
            var live = await sourceUnderTest.Task;
            await WaitUntilAsync(() => live.Readiness.TryGet("X", out var recoveryPublished) &&
                                       recoveryPublished.State == VolumeWatchState.CatchingUp);
            faultedAfterCatchUp.SetResult();
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task FailedMarkerPath_DeclinesTheEntireCapture(bool failBaseline, bool invalidOperation)
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("firstrepo");
        files.AddMarker("lastrepo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = PackedIndexFixture.Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        source.StartStartupBuffering();
        Exception failure = invalidOperation
            ? new InvalidOperationException("The drive block has no configured root.")
            : new InvalidDataException("The parent chain exceeds the supported depth.");
        var pathsRead = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanStarted = HoldRecoveryScan(files, release.Task);
        if (failBaseline)
        {
            source.RepositoryMarkerReadingForTest = FailLastPath;
        }

        scripted.WatchFor('X').FailDrive(new IOException("X failed"));
        await scanStarted.WaitAsync(TestTimeouts.HangGuard);
        source.RepositoryMarkerReadingForTest = failBaseline ? null : FailLastPath;
        release.SetResult();
        await WaitForRecoveredWorkAsync(source, owner.Index);

        Assert.That(pathsRead.Select(Path.GetFileName), Is.EqualTo([".git", ".git"]),
            "The failure occurs after one marker has already been materialized.");
        var buffered = source.CompleteStartupBuffer();
        Assert.That(buffered, Is.Empty, "A partial inventory must never become repository events.");
        var foundReadiness = source.Readiness.TryGet("X", out var readiness);
        Assert.That(foundReadiness, Is.True);
        var readinessState = readiness!.State;
        Assert.That(readinessState, Is.EqualTo(VolumeWatchState.Excluded));
        var exclusionReason = readiness.ExclusionReason;
        Assert.That(exclusionReason, Is.EqualTo(failure.Message));
        return;

        void FailLastPath(FileEntry entry)
        {
            pathsRead.Add(entry.Path);
            if (entry.Parent!.Value.Name == "lastrepo")
            {
                throw failure;
            }
        }
    }

    [Test]
    public async Task DisposalDuringTheLastMarkerRead_DeclinesReconciliationWithoutFailingTheStream()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("firstrepo");
        files.AddMarker("lastrepo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = PackedIndexFixture.Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        source.StartStartupBuffering();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanStarted = HoldRecoveryScan(files, release.Task);
        scripted.WatchFor('X').FailDrive(new IOException("X failed"));
        await scanStarted.WaitAsync(TestTimeouts.HangGuard);
        var pathsRead = new List<string>();
        source.RepositoryMarkerReadingForTest = entry =>
        {
            pathsRead.Add(entry.Path);
            ObjectDisposedException.ThrowIf(entry.Parent!.Value.Name == "lastrepo", typeof(FileEntry));
        };
        release.SetResult();
        await WaitForRecoveredWorkAsync(source, owner.Index);

        Assert.That(pathsRead, Has.Count.EqualTo(2));
        var buffered = source.CompleteStartupBuffer();
        Assert.That(buffered, Is.Empty);
        var foundReadiness = source.Readiness.TryGet("X", out var readiness);
        Assert.That(foundReadiness, Is.True);
        var readinessState = readiness!.State;
        Assert.That(readinessState, Is.EqualTo(VolumeWatchState.CatchingUp),
            "Disposal must reach the declined-capture handler, not publish CaughtUp or fail the stream.");

        // A later Live event proves disposal was handled as a declined read rather than a stream failure.
        source.RepositoryMarkerReadingForTest = null;
        await using var reader = source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        var marker = owner.Index.Search(new SearchQuery(".git", NameMatchMode.Exact))[0];
        await scripted.WatchFor('X').PublishBatchAsync([
            PackedIndexFixture.Entry(marker.RecordKey.RecordNumber, marker.Parent!.Value.RecordKey.RecordNumber,
                ".git", SyntheticJournalReason.FileDelete, directory: true)
        ], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1002));
        var delivered = await reader.MoveNextAsync().AsTask().WaitAsync(TestTimeouts.HangGuard);
        Assert.That(delivered, Is.True);
        var deletedEntry = reader.Current.Entries.Single();
        Assert.That(deletedEntry.Kind, Is.EqualTo(VolumeEntryKind.Deleted));
    }

    [Test]
    public async Task RowReuseDuringRecoveryPathRead_PreservesReconciliationCaughtUpAndLaterLiveDelivery()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("supersededrepo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = PackedIndexFixture.Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanStarted = HoldRecoveryScan(files, release.Task);
        scripted.WatchFor('X').FailDrive(new IOException("X failed"));
        await scanStarted.WaitAsync(TestTimeouts.HangGuard);
        files.AddMarker("newrepo");
        Task? reuseDelivered = null;
        source.RepositoryMarkerReadingForTest = entry =>
        {
            if (entry.Parent!.Value.Name != "supersededrepo")
            {
                return;
            }

            var deletion = PackedIndexFixture.Entry(entry.RecordKey.RecordNumber,
                entry.Parent!.Value.RecordKey.RecordNumber, ".git", SyntheticJournalReason.FileDelete, directory: true);
            reuseDelivered = scripted.WatchFor('X').QueueBatchAsync([
                deletion,
                deletion with { SequenceNumber = 2, FileName = "replacement", Reason = SyntheticJournalReason.FileCreate,
                    Usn = 1002 }
            ], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1003));
            Assert.That(SpinWait.SpinUntil(() => owner.Index.Search(
                new SearchQuery("replacement", NameMatchMode.Exact)).Count == 1, TestTimeouts.HangGuard), Is.True);
            var deleted = entry.IsDeleted;
            Assert.That(deleted, Is.True);
            Assert.Throws<InvalidOperationException>(() => _ = entry.Path,
                "The selected handle must be superseded before the production path read.");
        };
        await using var reader = source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        release.SetResult();

        var reconciled = await reader.MoveNextAsync().AsTask().WaitAsync(TestTimeouts.HangGuard);
        Assert.That(reconciled, Is.True);
        var reconciliationEntries = reader.Current.Entries;
        Assert.That(reconciliationEntries, Is.EquivalentTo([
            new VolumeChangeEntry(@"X:\supersededrepo\.git", VolumeEntryKind.Deleted),
            new VolumeChangeEntry(@"X:\newrepo\.git", VolumeEntryKind.Created)
        ]));
        await WaitForRecoveredWorkAsync(source, owner.Index);
        var foundReadiness = source.Readiness.TryGet("X", out var readiness);
        Assert.That(foundReadiness, Is.True);
        var readinessState = readiness!.State;
        Assert.That(readinessState, Is.EqualTo(VolumeWatchState.CaughtUp));
        var reuseTask = reuseDelivered;
        Assert.That(reuseTask, Is.Not.Null);
        await reuseTask!.WaitAsync(TestTimeouts.HangGuard);
        var reusedDeletion = await reader.MoveNextAsync().AsTask().WaitAsync(TestTimeouts.HangGuard);
        Assert.That(reusedDeletion, Is.True);
        var reusedDeletionEntry = reader.Current.Entries.Single();
        Assert.That(reusedDeletionEntry, Is.EqualTo(
            new VolumeChangeEntry(@"X:\supersededrepo\.git", VolumeEntryKind.Deleted)));

        var replacement = await reader.MoveNextAsync().AsTask().WaitAsync(TestTimeouts.HangGuard);
        Assert.That(replacement, Is.True);
        var replacementEntry = reader.Current.Entries.Single();
        Assert.That(replacementEntry, Is.EqualTo(
            new VolumeChangeEntry(@"X:\supersededrepo\replacement", VolumeEntryKind.Created)));

        var marker = owner.Index.Search(new SearchQuery(".git", NameMatchMode.Exact)).Single();
        await scripted.WatchFor('X').PublishBatchAsync([
            PackedIndexFixture.Entry(marker.RecordKey.RecordNumber, marker.Parent!.Value.RecordKey.RecordNumber,
                ".git", SyntheticJournalReason.FileDelete, directory: true) with { Usn = 1004 }
        ], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1005));
        var finalDelivery = await reader.MoveNextAsync().AsTask().WaitAsync(TestTimeouts.HangGuard);
        Assert.That(finalDelivery, Is.True);
        var finalEntry = reader.Current.Entries.Single();
        Assert.That(finalEntry, Is.EqualTo(
            new VolumeChangeEntry(@"X:\newrepo\.git", VolumeEntryKind.Deleted)));
    }

    static Task HoldRecoveryScan(PackedIndexFixture files, Task release)
    {
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeProduceAsync = (_, token) =>
        {
            scanStarted.TrySetResult();
            return release.WaitAsync(token);
        };
        return scanStarted.Task;
    }

    static async Task WaitForRecoveredWorkAsync(IndexVolumeChangeSource source, FileIndex index)
    {
        await WaitUntilAsync(() => index.Drives.Single().Watch.State == WatchState.CaughtUp &&
            source.Readiness.TryGet("X", out var status) &&
            (status.State == VolumeWatchState.Excluded || status.WatchStateVersion == index.Drives.Single().Watch.StateVersion));
        await source.WaitForCatchUpWorkForTestAsync().WaitAsync(TestTimeouts.HangGuard);
    }

    static async Task ConsumeAsync(RepositoryWatchService service, List<RepositoryChangeEvent> events,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var change in service.RunAsync(cancellationToken))
            {
                lock (events)
                {
                    events.Add(change);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The test ends the run by cancelling it.
        }
    }
}
