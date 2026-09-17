using GitWizard;
using GitWizard.Watch;
using MFTLib;
using MFTLib.Index;

namespace GitWizardTests.TestSupport;

internal static class WatchFailureAssertions
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
                await stopping;
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
            if (scenario == "arm")
                source.Arming = () => throw new InvalidOperationException("arming failed");
            if (scenario == "started")
                service.Started += () => throw new InvalidOperationException("subscriber failed");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var reader = service.RunAsync(cancellation.Token).GetAsyncEnumerator();
            var pending = reader.MoveNextAsync().AsTask();
            if (scenario is "arm" or "started")
                Assert.ThrowsAsync<InvalidOperationException>(async () => await pending);
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
            Assert.That(source.DiagnosticSubscribers, Is.EqualTo(1));
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
        Assert.That(await restarted.MoveNextAsync(), Is.False);
        Assert.That(service.ScanErrors, Is.Empty, "A new run must not retain the previous run's errors.");
    }

    public static async Task VerifyQueryableIndexAsync()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedIndexWatchSource();
        var index = await files.OpenMftIndexAsync(scripted, 'X', 'Y');
        var owner = new MftIndexSession(index, null, ["X", "Y"], [], new Dictionary<string, string>());
        var source = new IndexVolumeChangeSource((_, _) => Task.FromResult(owner));
        try
        {
            var service = new RepositoryWatchService(source, [@"X:\repo", @"Y:\repo"], [], TimeSpan.Zero);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deaths = new List<string>();
            service.ScanErrorsChanged += () =>
            {
                if (service.ScanErrors.ContainsKey("X"))
                    failed.TrySetResult();
            };
            service.Stopped += deaths.Add;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var reader = service.RunAsync(cancellation.Token).GetAsyncEnumerator();
            var pending = reader.MoveNextAsync().AsTask();
            await scripted.Started.WaitAsync(cancellation.Token);
            scripted.Publish(new DriveWatchFailure('X', new IOException("X watch failed")));
            await failed.Task.WaitAsync(cancellation.Token);
            Assert.That(service.ScanErrors["X"], Is.EqualTo("X watch failed"));
            Assert.That(index.Drives.Single(drive => drive.DriveLetter == 'X').State, Is.EqualTo(DriveState.Ready));
            var markers = index.FindByName(".git").ToArray();
            Assert.That(markers, Has.Length.EqualTo(2), "Both the failed and healthy drive remain queryable.");
            Assert.That(markers.All(entry => entry.Parent is { Name: "repo" }), Is.True);
            Assert.That(deaths, Is.Empty);
            scripted.Publish(new JournalBatch('Y',
                [UsnJournalEntry.Create(new UsnJournalEntryOptions
                {
                    RecordNumber = 50, ParentRecordNumber = 6, SequenceNumber = 1,
                    FileName = "file", Reason = UsnReason.FileCreate,
                    FileAttributes = FileAttributes.Normal,
                    Timestamp = PackedIndexFixture.CompletionTimestamp, Usn = 1001
                })], PackedIndexFixture.JournalId, 1002));
            Assert.That(await pending, Is.True);
            Assert.That(reader.Current.RepoRoot, Is.EqualTo(@"Y:\repo"));
            pending = reader.MoveNextAsync().AsTask();
            scripted.Publish(new DriveWatchFailure('Y', new IOException("Y watch failed")));
            Assert.That(await pending, Is.False);
            Assert.That(deaths, Has.Count.EqualTo(1));
            Assert.That(service.ScanErrors["Y"], Is.EqualTo("Y watch failed"));
        }
        finally
        {
            await source.DisposeAsync();
        }
        Assert.That(owner.IsDisposed, Is.True);
    }
}
