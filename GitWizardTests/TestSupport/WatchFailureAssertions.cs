using GitWizard;
using GitWizard.Watch;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

static class WatchFailureAssertions
{
    public static async Task VerifyEarlyStopAsync()
    {
        await using var source = new DiagnosticVolumeChangeSource();
        var service = new RepositoryWatchService(source, [@"Y:\repo"], [], TimeSpan.Zero);
        var reader = service.RunAsync(CancellationToken.None).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        Task? stopping = null;
        try
        {
            await source.Started.WaitAsync(TimeSpan.FromSeconds(5));
            source.Publish(new VolumeChangeBatch("Y",
                [new VolumeChangeEntry(@"Y:\repo\file", VolumeEntryKind.Modified)]));
            Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            stopping = reader.DisposeAsync().AsTask();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            source.Complete();
            if (stopping is not null)
            {
                await stopping;
            }
            else
            {
                try
                {
                    await pending;
                }
                finally
                {
                    await reader.DisposeAsync();
                }
            }
        }
        Assert.That(source.DiagnosticSubscribers, Is.Zero);
    }

    public static async Task VerifyShutdownAsync()
    {
        foreach (var scenario in new[] { "arm", "started", "watch", "cancel", "terminal", "complete" })
        {
            await using var source = new DiagnosticVolumeChangeSource();
            var service = new RepositoryWatchService(source, [], []);
            switch (scenario)
            {
                case "arm":
                    source.Arming = () => throw new InvalidOperationException("arming failed");
                    break;
                case "started":
                    service.Started += () => throw new InvalidOperationException("subscriber failed");
                    break;
            }

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var reader = service.RunAsync(cancellation.Token).GetAsyncEnumerator();
            var pending = reader.MoveNextAsync().AsTask();
            if (scenario is "arm" or "started")
            {
                Assert.ThrowsAsync<InvalidOperationException>(async () => await pending);
            }
            else
            {
                await source.Started.WaitAsync(cancellation.Token);
                switch (scenario)
                {
                    case "watch":
                        source.Fail(new IOException("watch failed"));
                        Assert.ThrowsAsync<IOException>(async () => await pending);
                        break;
                    case "cancel":
                        await cancellation.CancelAsync();
                        Assert.CatchAsync<OperationCanceledException>(async () => await pending);
                        break;
                    case "terminal":
                        source.KillSource("all drives lost");
                        Assert.That(await pending, Is.False);
                        break;
                    default:
                        source.Complete();
                        Assert.That(await pending, Is.False);
                        break;
                }
            }
            Assert.That(source.DiagnosticSubscribers, Is.Zero, scenario);
            var snapshot = service.ScanErrors;
            source.FailDrive("X", "late failure");
            Assert.That(service.ScanErrors, Is.SameAs(snapshot), scenario);
        }
    }

    public static async Task VerifyArmingSnapshotAsync()
    {
        await using var source = new DiagnosticVolumeChangeSource();
        await VerifyArmingSnapshotCoreAsync(source);
    }

    static async Task VerifyArmingSnapshotCoreAsync(DiagnosticVolumeChangeSource source)
    {
        source.ArmErrors["X"] = "scan failed";
        source.Arming = () =>
        {
            // DriveFailed and JournalWarningDetected: the service holds both diagnostics
            // subscriptions for the run's lifetime.
            Assert.That(source.DiagnosticSubscribers, Is.EqualTo(2));
            source.FailDrive("X", "newer watch failure");
        };
        var service = new RepositoryWatchService(source, [], []);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = service.RunAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await source.Started.WaitAsync(cancellation.Token);
        source.ArmErrors["Y"] = "mutated after arming";
        Assert.That(service.ScanErrors, Does.ContainKey("X").WithValue("newer watch failure"));
        Assert.That(service.ScanErrors.ContainsKey("Y"), Is.False);
        source.Complete();
        Assert.That(await pending, Is.False);
        source.Arming = null;
        source.ArmErrors.Clear();
        await using var restarted = service.RunAsync(cancellation.Token).GetAsyncEnumerator();
        var restartedHasEntries = await restarted.MoveNextAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restartedHasEntries, Is.False);
            Assert.That(service.ScanErrors, Is.Empty, "A new run must not retain the previous run's errors.");
        }
    }

    public static async Task VerifyQueryableIndexAsync()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var index = await files.OpenMftIndexAsync(scripted, 'X', 'Y');
        var owner = new MftIndexSession(index, null, ["X", "Y"], [], new Dictionary<string, string>());
        var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        try
        {
            var service = new RepositoryWatchService(source, [@"X:\repo", @"Y:\repo"], [], TimeSpan.Zero);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deaths = new List<string>();
            service.ScanErrorsChanged += () =>
            {
                if (service.ScanErrors.ContainsKey("X"))
                {
                    failed.TrySetResult();
                }
            };
            service.Stopped += deaths.Add;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var reader = service.RunAsync(cancellation.Token).GetAsyncEnumerator();
            var pending = reader.MoveNextAsync().AsTask();
            await Task.WhenAll(scripted.WaitForStartAsync('X', 1, cancellation.Token),
                scripted.WaitForStartAsync('Y', 1, cancellation.Token));
            scripted.WatchFor('X').LoseChannel(new IOException("X watch failed"));
            await failed.Task.WaitAsync(cancellation.Token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(service.ScanErrors["X"], Is.EqualTo("X watch failed"));
                Assert.That(index.Drives.Single(drive => drive.Drive == 'X').State, Is.EqualTo(DriveState.Ready));
            }
            var markers = index.Search(new SearchQuery(".git", NameMatchMode.Exact)).ToArray();
            Assert.That(markers, Has.Length.EqualTo(2), "Both the failed and healthy drive remain queryable.");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(markers.All(entry => entry.Parent is { Name: "repo" }), Is.True);
                Assert.That(deaths, Is.Empty);
            }
            await scripted.WatchFor('Y').PublishBatchAsync([new SyntheticJournalRecord
                {
                    RecordNumber = 50, ParentRecordNumber = 6, SequenceNumber = 1,
                    FileName = "file", Reason = SyntheticJournalReason.FileCreate,
                    FileAttributes = FileAttributes.Normal,
                    Timestamp = PackedIndexFixture.CompletionTimestamp, Usn = 1001
                }], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1002));
            var pendingResult = await pending;
            Assert.That(pendingResult, Is.True);
            var current = reader.Current;
            Assert.That(current.RepoRoot, Is.EqualTo(@"Y:\repo"));
            pending = reader.MoveNextAsync().AsTask();
            scripted.WatchFor('Y').LoseChannel(new IOException("Y watch failed"));
            Assert.That(await pending, Is.False);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(deaths, Has.Count.EqualTo(1));
                Assert.That(service.ScanErrors["Y"], Is.EqualTo("Y watch failed"));
            }
        }
        finally
        {
            await source.DisposeAsync();
        }
        Assert.That(owner.IsDisposed, Is.True);
    }
}
