using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;

namespace GitWizardTests.Watch;

[TestFixture]
public class IndexVolumeChangeSourceTests
{

    [Test]
    public async Task GrowUsnJournalAsync_RequiresBrokerConnection()
    {
        var owner = new IndexOwnerFixture();
        try
        {
            await owner.StartAsync();
            var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner.Owner), owner.Owner);
            var exception = Assert.ThrowsAsync<InvalidOperationException>(() => source.GrowUsnJournalAsync('C', 1000, 100, CancellationToken.None));
            Assert.That(exception.Message, Is.EqualTo("No broker connection available."));
        }
        finally
        {
            await owner.DisposeAsync();
        }
    }

    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    [Test]
    public async Task RealJournalRenameDivergesEntryPathFromChangePath()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedIndexWatchSource();
        await using var index = await files.OpenMftIndexAsync(scripted, 'X');
        var observed = new TaskCompletionSource<FileChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.Changed += change => observed.TrySetResult(change);
        await index.StartWatchingAsync(CancellationToken.None);
        await scripted.Started.WaitAsync(HangGuard);
        scripted.Publish(new JournalBatch('X',
        [
            Entry(6, 5, "first", UsnReason.RenameNewName, true),
            Entry(6, 5, "second", UsnReason.RenameNewName, true)
        ], PackedIndexFixture.JournalId, 1002));
        var change = await observed.Task.WaitAsync(HangGuard);
        await index.StopWatchingAsync(CancellationToken.None);
        await TestContext.Out.WriteLineAsync($"Entry.Path = '{change.Entry.Path}'; Path = '{change.Path}'");
        // This test subscribes to MFTLib's raw FileIndex.Changed event directly, bypassing
        // IndexVolumeChangeSource's drive-letter-root rewrite, so every path here renders
        // MFTLib's real indexed root directory rather than the synthetic "X:\" shape (MFTLib #143).
        var root = index.Root('X').Path;
        Assert.That(change.Kind, Is.EqualTo(FileChangeKind.Renamed));
        Assert.That(change.Entry.Path, Is.Not.EqualTo(change.Path));
        Assert.That(change.Path, Is.EqualTo(Path.Combine(root, "first")));
        Assert.That(change.Entry.Path, Is.EqualTo(Path.Combine(root, "second")));
        Assert.That(change.PreviousPath, Is.EqualTo(Path.Combine(root, "repo")));
    }

    [Test]
    public async Task ReusesMatchingPreparedIndex()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var connection = new CountingOwner();
        var owner = new MftIndexSession(await files.OpenIndexAsync(), connection, ["X"], [],
            new Dictionary<string, string> { ["Z"] = "scan denied" });
        var source = new IndexVolumeChangeSource((_, _, _) =>
            throw new AssertionException("Matching prepared owner must be reused."), owner);
        try
        {
            var result = await source.ArmAndCatchUpAsync([@"x:\", "X:"], CancellationToken.None);
            Assert.That(result.Errors["Z"], Is.EqualTo("scan denied"));
            Assert.That(owner.IsDisposed, Is.False);
            Assert.That(owner.Index.FindByName(".git").Single().Parent!.Value.Name, Is.EqualTo("repo"));
            Assert.That(owner.Index.Drives.Single().WatchSupported, Is.False);
            await using var reader = source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
            Assert.That(await reader.MoveNextAsync(), Is.False, "Enumeration ownership does not start a live pump.");
            await source.DisposeAsync();
            await source.DisposeAsync();
            Assert.That(connection.Disposals, Is.EqualTo(1));
            Assert.That(owner.IsDisposed, Is.True);
            Assert.ThrowsAsync<ObjectDisposedException>(() => source.ArmAndCatchUpAsync(["X"], CancellationToken.None));
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    [Test]
    public async Task MismatchDisposesAndOpensRequestedDrives()
    {
        using var files = new PackedIndexFixture();
        foreach (var disposePrepared in new[] { false, true })
        {
            var oldOwner = Owner(await files.OpenIndexAsync(), "X");
            if (disposePrepared)
                await oldOwner.DisposeAsync();
            var requested = disposePrepared ? "X" : "Y";
            var fresh = Owner(await files.OpenIndexAsync(), requested);
            var opens = 0;
            await using (var source = new IndexVolumeChangeSource((drives, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.That(oldOwner.IsDisposed, Is.True);
                Assert.That(drives, Is.EqualTo(new[] { requested }));
                opens++;
                return Task.FromResult(fresh);
            }, oldOwner))
            {
                await source.ArmAndCatchUpAsync([requested.ToLowerInvariant() + ":"], CancellationToken.None);
                Assert.That(opens, Is.EqualTo(1));
                Assert.That(fresh.IsDisposed, Is.False);
                Assert.ThrowsAsync<InvalidOperationException>(() =>
                    source.ArmAndCatchUpAsync([requested], CancellationToken.None));
            }
            Assert.That(fresh.IsDisposed, Is.True);
        }
        await using var failedIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('X', files.TreePath, 1)],
            CacheDirectory = files.BlockDirectory,
            NoCache = true,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = (_, _) => throw new IOException("cannot scan X")
        }, CancellationToken.None);
        var failedOwner = Owner(failedIndex, "X");
        await using var failedSource = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(failedOwner));
        var failedArm = await failedSource.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        Assert.That(failedArm.Errors["X"], Is.EqualTo("cannot scan X"));
        var deaths = new List<string>();
        failedSource.SourceDied += deaths.Add;
        await using var reader = failedSource.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.That(await reader.MoveNextAsync(), Is.False, "A wholly failed open must stop cleanly.");
        Assert.That(deaths, Has.Count.EqualTo(1));
        Assert.That(deaths[0], Is.EqualTo("No requested drives could be watched."));
    }

    [Test]
    public async Task DisposeBeforeArmClosesPreparedOwner()
    {
        using var files = new PackedIndexFixture();
        var connection = new CountingOwner();
        var owner = new MftIndexSession(await files.OpenIndexAsync(), connection, ["X"], [],
            new Dictionary<string, string>());
        var opens = 0;
        var source = new IndexVolumeChangeSource((_, _, _) =>
        {
            opens++;
            return Task.FromResult(owner);
        }, owner);
        await source.DisposeAsync();
        await source.DisposeAsync();
        Assert.That(owner.IsDisposed, Is.True);
        Assert.That(connection.Disposals, Is.EqualTo(1));
        Assert.That(opens, Is.Zero);
    }

    [Test]
    public async Task WatchDeliversMutationEvents()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedIndexWatchSource();
        var owner = Owner(await files.OpenMftIndexAsync(scripted, 'X', 'Y'), "X", "Y");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X", "Y"], CancellationToken.None);
        Assert.That(scripted.Started.IsCompleted, Is.True, "Arming must start the watch.");
        var targets = await scripted.Started.WaitAsync(HangGuard);
        using var cancellation = new CancellationTokenSource(HangGuard);
        await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        Assert.That(targets, Is.EquivalentTo(new[]
        {
            new IndexWatchTarget('X', PackedIndexFixture.JournalId, PackedIndexFixture.NextUsn),
            new IndexWatchTarget('Y', PackedIndexFixture.JournalId, PackedIndexFixture.NextUsn)
        }));
        // Routed through IndexVolumeChangeSource, which rewrites every path back onto its own
        // configured drive letter (MFTLib #143), so these stay the pre-existing synthetic shape.
        scripted.Publish(new JournalBatch('X',
        [
            Entry(50, 6, "file.txt", UsnReason.FileCreate),
            Entry(50, 6, "renamed.txt", UsnReason.RenameNewName),
            Entry(50, 6, "renamed.txt", UsnReason.FileDelete)
        ], PackedIndexFixture.JournalId, 1004));
        Assert.That(await firstRead, Is.True);
        Assert.That(reader.Current, Is.EqualTo(new VolumeChangeBatch("X", reader.Current.Entries)));
        Assert.That(reader.Current.Entries, Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"X:\repo\file.txt", VolumeEntryKind.Created)
        }));
        Assert.That(await reader.MoveNextAsync(), Is.True);
        Assert.That(reader.Current.Entries, Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"X:\repo\file.txt", VolumeEntryKind.Deleted),
            new VolumeChangeEntry(@"X:\repo\renamed.txt", VolumeEntryKind.Created)
        }));
        Assert.That(await reader.MoveNextAsync(), Is.True);
        Assert.That(reader.Current.Entries.Single(), Is.EqualTo(
            new VolumeChangeEntry(@"X:\repo\renamed.txt", VolumeEntryKind.Deleted)));
        scripted.Publish(new JournalBatch('Y', [Entry(51, 6, "other.txt", UsnReason.FileCreate)],
            PackedIndexFixture.JournalId, 1002));
        Assert.That(await reader.MoveNextAsync(), Is.True);
        Assert.That(reader.Current.Volume, Is.EqualTo("Y"));
        Assert.That(reader.Current.Entries.Single().FullPath, Is.EqualTo(@"Y:\repo\other.txt"));
        Assert.That(owner.Index.FindByName("other.txt").Single().Parent!.Value.Name, Is.EqualTo("repo"));
    }

    [Test]
    public Task OrdinaryFileModificationHydratesOmittedRow() =>
        AssertHydrationAsync(UsnReason.DataOverwrite, VolumeEntryKind.Modified);

    [Test]
    public Task OrdinaryFileDeletionHydratesOmittedRow() =>
        AssertHydrationAsync(UsnReason.FileDelete, VolumeEntryKind.Deleted);

    static async Task AssertHydrationAsync(UsnReason reason, VolumeEntryKind expected)
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        await File.WriteAllTextAsync(Path.Combine(files.TreePath, "repo", "ordinary.txt"), "omitted by profile");
        var scripted = new ScriptedIndexWatchSource();
        var owner = Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
        Assert.That(owner.Index.FindByName("ordinary.txt"), Is.Empty);
        var observed = new TaskCompletionSource<FileChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Index.Changed += change => observed.TrySetResult(change);
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        using var cancellation = new CancellationTokenSource(HangGuard);
        await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await scripted.Started.WaitAsync(HangGuard);
        scripted.Publish(new JournalBatch('X', [Entry(50, 6, "ordinary.txt", reason)],
            PackedIndexFixture.JournalId, 1002));
        Assert.That(await pending, Is.True);
        Assert.That(reader.Current.Volume, Is.EqualTo("X"));
        // Routed through IndexVolumeChangeSource, which rewrites this back onto its own
        // configured drive letter (MFTLib #143), so this stays the pre-existing synthetic shape.
        Assert.That(reader.Current.Entries.Single(), Is.EqualTo(new VolumeChangeEntry(@"X:\repo\ordinary.txt", expected)));
        var change = await observed.Task.WaitAsync(HangGuard);
        Assert.That(change.Entry.Name, Is.EqualTo("ordinary.txt"));
        Assert.That(change.Entry.IsDirectory, Is.False);
        Assert.That(change.Entry.Parent!.Value.Name, Is.EqualTo("repo"));
        Assert.That(change.Entry.IsDeleted, Is.EqualTo(expected == VolumeEntryKind.Deleted));
    }

    [Test]
    public async Task CancellationStopsPumpAndCompletesReader()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        foreach (var disposeSource in new[] { false, true })
        {
            var scripted = new ScriptedIndexWatchSource();
            var owner = Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
            var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
            var deaths = 0;
            source.SourceDied += _ => deaths++;
            try
            {
                await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
                using var cancellation = new CancellationTokenSource(HangGuard);
                await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();
                var pending = reader.MoveNextAsync().AsTask();
                await scripted.Started.WaitAsync(HangGuard);
                if (disposeSource)
                {
                    await source.DisposeAsync();
                    Assert.That(await pending, Is.False);
                    Assert.That(owner.IsDisposed, Is.True);
                }
                else
                {
                    await cancellation.CancelAsync();
                    Assert.CatchAsync<OperationCanceledException>(async () => await pending);
                }
                await scripted.Completed.WaitAsync(HangGuard);
                Assert.That(scripted.WasCancelled, Is.True);
                Assert.That(deaths, Is.Zero);
                await source.DisposeAsync();
                Assert.That(owner.IsDisposed, Is.True);
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task TerminalFaultClosesChannelOnce()
    {
        foreach (var scenario in new[] { "merged", "ended", "drive", "apply", "subscriber" })
            await AssertFaultAsync(scenario);
    }

    static async Task AssertFaultAsync(string scenario)
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedIndexWatchSource();
        var owner = Owner(await files.OpenMftIndexAsync(scripted, 'X', 'Y'), "X", "Y");
        var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        try
        {
            await AssertFaultCoreAsync(source, scripted, owner, scenario);
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    static async Task AssertFaultCoreAsync(
        IndexVolumeChangeSource source,
        ScriptedIndexWatchSource scripted,
        MftIndexSession owner,
        string scenario)
    {
        var deaths = new List<string>();
        var failures = new List<(string Drive, string Message)>();
        var driveFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SourceDied += deaths.Add;
        source.DriveFailed += (drive, message) =>
        {
            failures.Add((drive, message));
            driveFailed.TrySetResult();
        };
        await source.ArmAndCatchUpAsync(["X", "Y"], CancellationToken.None);
        using var cancellation = new CancellationTokenSource(HangGuard);
        await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await scripted.Started.WaitAsync(HangGuard);
        switch (scenario)
        {
            case "merged":
                scripted.Fail(new IOException("merged stream failed"));
                break;
            case "ended":
                scripted.Complete();
                break;
            case "subscriber":
                owner.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");
                scripted.Publish(new JournalBatch('X', [Entry(50, 6, "changed", UsnReason.FileCreate)],
                    PackedIndexFixture.JournalId, 1002));
                Assert.That(await pending, Is.True);
                var exception = Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.MoveNextAsync());
                Assert.That(exception!.Message, Is.EqualTo("subscriber failed"));
                Assert.That(deaths, Is.Empty);
                Assert.That(failures, Is.Empty);
                return;
            default:
                if (scenario == "drive")
                    scripted.Publish(new DriveWatchFailure('X', new IOException("X failed")));
                else
                    scripted.Publish(new JournalBatch('X', null!, PackedIndexFixture.JournalId, 1002));
                await driveFailed.Task.WaitAsync(HangGuard);
                Assert.That(deaths, Is.Empty);
                Assert.That(failures.Single().Drive, Is.EqualTo("X"));
                Assert.That(failures.Single().Message, Is.EqualTo(owner.Index.Drives.Single(
                    drive => drive.DriveLetter == 'X').WatchFailureMessage));
                scripted.Publish(new JournalBatch('Y', [Entry(50, 6, "survivor", UsnReason.FileCreate)],
                    PackedIndexFixture.JournalId, 1002));
                Assert.That(await pending, Is.True);
                Assert.That(reader.Current.Volume, Is.EqualTo("Y"));
                pending = reader.MoveNextAsync().AsTask();
                scripted.Publish(new DriveWatchFailure('Y', new IOException("Y failed")));
                break;
        }
        Assert.That(await pending, Is.False);
        Assert.That(deaths, Has.Count.EqualTo(1));
        if (scenario == "merged")
            Assert.That(deaths.Single(), Is.EqualTo("merged stream failed"));
        if (scenario == "ended")
            Assert.That(deaths.Single(), Does.Contain("ended its stream without being stopped"));
        await scripted.Completed.WaitAsync(HangGuard);
        await source.DisposeAsync();
        await source.DisposeAsync();
        Assert.That(deaths, Has.Count.EqualTo(1));
        Assert.That(owner.IsDisposed, Is.True);
    }

    [Test]
    public async Task FreshOpen_ReportsScanProgressWithDriveOrdinal()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var owner = Owner(await files.OpenIndexAsync(), "X", "Y");
        IProgress<IndexScanProgress>? capturedProgress = null;
        await using var source = new IndexVolumeChangeSource((drives, progress, _) =>
        {
            Assert.That(drives, Is.EqualTo(new[] { "X", "Y" }));
            capturedProgress = progress;
            return Task.FromResult(owner);
        });
        var samples = new List<VolumeScanProgress>();
        source.ScanProgress += samples.Add;

        await source.ArmAndCatchUpAsync([@"x:\", "Y:"], CancellationToken.None);

        Assert.That(capturedProgress, Is.Not.Null);
        capturedProgress!.Report(new IndexScanProgress
        {
            DriveLetter = 'y',
            Phase = IndexScanPhase.ParsingMft,
            RowsWritten = 600_000,
            TotalRows = 1_200_000
        });
        Assert.That(samples, Is.EqualTo(new[]
        {
            new VolumeScanProgress("Y", 2, 2, 600_000u, 1_200_000u)
        }));
    }

    static MftIndexSession Owner(FileIndex index, params string[] drives) =>
        new(index, null, drives, [], new Dictionary<string, string>());

    [Test]
    public async Task CacheDeclinedDriveSurfacesCheckpointLossHint()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var owner = new MftIndexSession(await files.OpenIndexAsync(), null, ["X"],
            [new IndexedDrive('X', files.TreePath, 12345u)], new Dictionary<string, string>());
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner), owner);
        var loss = new JournalCheckpointLoss
        {
            DriveLetter = 'X',
            DetectedDuring = JournalCheckpointLossDetection.DriveOpening,
            Cause = JournalCheckpointLossCause.CheckpointTrimmed,
            CheckpointUsn = 1_000_000,
            FirstUsn = 5_000_000,
            NextUsn = 10_000_000,
            AllocationDelta = 8 * 1024 * 1024,
            MaximumSize = 32L * 1024 * 1024,
            BytesBehind = 4_000_000,
            SizeThatWouldHaveRetained = 136L * 1024 * 1024
        };
        var status = new DriveStatus
        {
            DriveLetter = 'X',
            ProducerKind = ProducerKind.Mft,
            BlockSource = BlockSource.None,
            State = DriveState.Failed,
            RowCount = 0,
            LiveRowCount = 0,
            ScanTimestamp = DateTime.UtcNow,
            CompactionNeeded = false,
            WatchSupported = false,
            FailureKind = DriveFailureKind.CacheDeclined,
            MftProducerFailureMessage = "cache declined",
            CheckpointLoss = loss
        };
        var errors = new Dictionary<string, string>();
        var warnings = new List<JournalWarning>();

        source.ProcessArmedDrive(status, new GitWizardConfiguration(), errors, warnings, CancellationToken.None);

        Assert.That(errors, Does.ContainKey("X").WithValue("cache declined"));
        var warning = warnings.Single();
        Assert.That(warning.DriveLetter, Is.EqualTo("X"));
        Assert.That(warning.Loss, Is.SameAs(loss));
        Assert.That(warning.VolumeSerial, Is.EqualTo(12345u));
    }

    [Test]
    public async Task FaultedDriveWithLiveWatchLoss_RaisesJournalWarning()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var owner = new MftIndexSession(await files.OpenIndexAsync(), null, ["X"],
            [new IndexedDrive('X', files.TreePath, 12345u)], new Dictionary<string, string>());
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner), owner);
        var warnings = new List<JournalWarning>();
        source.JournalWarningDetected += warnings.Add;
        var liveLoss = FakeJournalSettingsSource.TrimmedLoss('X', 32L * 1024 * 1024, 136L * 1024 * 1024,
            detectedDuring: JournalCheckpointLossDetection.LiveWatch);
        var status = new DriveStatus
        {
            DriveLetter = 'X',
            ProducerKind = ProducerKind.Mft,
            BlockSource = BlockSource.ProducedByScan,
            State = DriveState.Ready,
            RowCount = 10,
            LiveRowCount = 10,
            ScanTimestamp = DateTime.UtcNow,
            CompactionNeeded = false,
            WatchSupported = true,
            WatchFailureMessage = "watch reached a position the journal no longer holds",
            CheckpointLoss = liveLoss
        };

        source.ProcessFaultedDrive(status, owner, new GitWizardConfiguration());

        var warning = warnings.Single();
        Assert.That(warning.DriveLetter, Is.EqualTo("X"));
        Assert.That(warning.Loss, Is.SameAs(liveLoss));
        Assert.That(warning.VolumeSerial, Is.EqualTo(12345u));

        // A fault the journal did not cause reports nothing: the open's standing report and a
        // missing report both stay out of the event.
        var openLoss = FakeJournalSettingsSource.TrimmedLoss('X', 32L * 1024 * 1024, 136L * 1024 * 1024);
        source.ProcessFaultedDrive(status with { CheckpointLoss = openLoss }, owner, new GitWizardConfiguration());
        source.ProcessFaultedDrive(status with { CheckpointLoss = null }, owner, new GitWizardConfiguration());
        Assert.That(warnings, Has.Count.EqualTo(1));
    }


    static UsnJournalEntry Entry(ulong record, ulong parent, string name, UsnReason reason,
        bool directory = false) => UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = record,
            ParentRecordNumber = parent,
            SequenceNumber = 1,
            FileName = name,
            Reason = reason,
            FileAttributes = directory ? FileAttributes.Directory : FileAttributes.Normal,
            Timestamp = PackedIndexFixture.CompletionTimestamp,
            Usn = 1001
        });
}
