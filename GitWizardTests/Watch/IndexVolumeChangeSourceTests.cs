using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;
using MFTLibTestExtensions;
using static GitWizardTests.TestSupport.PackedIndexFixture;

namespace GitWizardTests.Watch;

[TestFixture]
public class IndexVolumeChangeSourceTests
{

    [Test]
    public async Task GrowUsnJournalAsyncLaunchesBrokerOnFirstUse()
    {
        await using var broker = new BlockBrokerFixture();
        broker.GrowUsnJournal = (_, maximumSize, allocationDelta) => new UsnJournalSettings
        {
            MaximumSize = maximumSize,
            AllocationDelta = allocationDelta
        };
        // Opening no drives never launches the broker; the grow is the session's first use.
        // The source owns the prepared owner, so it is not using-declared here.
        var owner = await MftIndexSession.OpenAsync([], broker.CreateSession(),
            broker.CacheDirectory, null, CancellationToken.None);
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner), owner);
        Assert.That(broker.ConnectionCount, Is.Zero);
        var settings = await source.GrowUsnJournalAsync('X',
                new UsnJournalSettings { MaximumSize = 4096, AllocationDelta = 512 }, CancellationToken.None)
            .WaitAsync(TestTimeouts.HangGuard);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.MaximumSize, Is.EqualTo(4096));
            Assert.That(settings.AllocationDelta, Is.EqualTo(512));
            Assert.That(broker.ConnectionCount, Is.EqualTo(1));
        }
    }


    [Test]
    public async Task RealJournalRenameDivergesEntryPathFromChangePath()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        await using var index = await files.OpenMftIndexAsync(scripted, 'X');
        var observed = new TaskCompletionSource<FileChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.Changed += change => observed.TrySetResult(change);
        await index.StartWatchingAsync(['X'], CancellationToken.None);
        await scripted.WaitForStartAsync('X', 1, CancellationToken.None).WaitAsync(TestTimeouts.HangGuard);
        await scripted.WatchFor('X').PublishBatchAsync([
            Entry(6, 5, "first", SyntheticJournalReason.RenameNewName, true),
            Entry(6, 5, "second", SyntheticJournalReason.RenameNewName, true)
        ], new SyntheticJournalCursor(JournalIdentifier, 1002));
        var change = await observed.Task.WaitAsync(TestTimeouts.HangGuard);
        await index.StopWatchingAsync(['X'], CancellationToken.None);
        await TestContext.Out.WriteLineAsync($"Entry.Path = '{change.Entry.Path}'; Path = '{change.Path}'");
        // This test subscribes to MFTLib's raw FileIndex.Changed event directly, bypassing
        // IndexVolumeChangeSource's drive-letter-root rewrite, so every path here renders
        // MFTLib's real indexed root directory rather than the synthetic "X:\" shape (MFTLib #143).
        var root = index.Root('X').Path;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Kind, Is.EqualTo(FileChangeKind.Renamed));
            Assert.That(change.Entry.Path, Is.Not.EqualTo(change.Path));
            Assert.That(change.Path, Is.EqualTo(Path.Combine(root, "first")));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Entry.Path, Is.EqualTo(Path.Combine(root, "second")));
            Assert.That(change.PreviousPath, Is.EqualTo(Path.Combine(root, "repo")));
        }
    }

    [TestCase(null)]
    [TestCase("Y:relative", TestName = "Issue370_Watch_RejectsDriveRelativePath")]
    [TestCase(@"Y:\repository", TestName = "Issue370_Watch_RejectsFullPathAsSpec")]
    [TestCase(@"\\server\share", TestName = "Issue370_Watch_RejectsUncSpec")]
    [TestCase("", TestName = "Issue370_Watch_IgnoresEmptySpec")]
    public async Task ReusesMatchingPreparedIndex(string? invalidSpec)
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
            string[] volumes = invalidSpec is null ? [@"x:\", "X:"] : [@"x:\", "X:", invalidSpec];
            var result = await source.ArmAndCatchUpAsync(volumes, CancellationToken.None);
            Assert.That(result.Errors["Z"], Is.EqualTo("scan denied"));
            Assert.That(owner.IsDisposed, Is.False);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(owner.Index.Search(new SearchQuery(".git", NameMatchMode.Exact)).Single().Parent!.Value.Name, Is.EqualTo("repo"));
                Assert.That(owner.Index.Drives.Single().Watch.IsSupported, Is.False);
            }
            await using var reader = source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
            Assert.That(await reader.MoveNextAsync(), Is.False, "Enumeration ownership does not start a live pump.");
            await source.DisposeAsync();
            await source.DisposeAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(connection.Disposals, Is.EqualTo(1));
                Assert.That(owner.IsDisposed, Is.True);
            }
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
            {
                await oldOwner.DisposeAsync();
            }

            var requested = disposePrepared ? "X" : "Y";
            var fresh = Owner(await files.OpenIndexAsync(), requested);
            var opens = 0;
            await using (var source = new IndexVolumeChangeSource((drives, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.That(oldOwner.IsDisposed, Is.True);
                Assert.That(drives, Is.EqualTo([requested]));
                opens++;
                return Task.FromResult(fresh);
            }, oldOwner))
            {
                await source.ArmAndCatchUpAsync([requested.ToLowerInvariant() + ":"], CancellationToken.None);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(opens, Is.EqualTo(1));
                    Assert.That(fresh.IsDisposed, Is.False);
                }
                Assert.ThrowsAsync<InvalidOperationException>(() =>
                    source.ArmAndCatchUpAsync([requested], CancellationToken.None));
            }
            Assert.That(fresh.IsDisposed, Is.True);
        }
        await using var failedIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('X', files.TreePath, 1)],
            CacheMode = CacheMode.Disabled,
            MftSource = SyntheticIndexSource.Create(new SyntheticMftProducer(_ => throw new IOException("cannot scan X")))
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(owner.IsDisposed, Is.True);
            Assert.That(connection.Disposals, Is.EqualTo(1));
            Assert.That(opens, Is.Zero);
        }
    }

    [Test]
    public async Task WatchDeliversMutationEvents()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = Owner(await files.OpenMftIndexAsync(scripted, 'X', 'Y'), "X", "Y");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X", "Y"], CancellationToken.None);
        var starts = scripted.Starts;
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        Assert.That(starts, Is.EquivalentTo([
            new ScriptedWatchStart('X', new SyntheticJournalCursor(JournalIdentifier, NextUsn)),
            new ScriptedWatchStart('Y', new SyntheticJournalCursor(JournalIdentifier, NextUsn))
        ]), "Arming must start the watch.");
        // Routed through IndexVolumeChangeSource, which rewrites every path back onto its own
        // configured drive letter (MFTLib #143), so these stay the pre-existing synthetic shape.
        await scripted.WatchFor('X').PublishBatchAsync([
            Entry(50, 6, "file.txt", SyntheticJournalReason.FileCreate),
            Entry(50, 6, "renamed.txt", SyntheticJournalReason.RenameNewName),
            Entry(50, 6, "renamed.txt", SyntheticJournalReason.FileDelete)
        ], new SyntheticJournalCursor(JournalIdentifier, 1004));
        Assert.That(await firstRead, Is.True);
        Assert.That(reader.Current, Is.EqualTo(reader.Current with { Volume = "X" }));
        Assert.That(reader.Current.Entries, Is.EqualTo([
            new VolumeChangeEntry(@"X:\repo\file.txt", VolumeEntryKind.Created)
        ]));
        Assert.That(await reader.MoveNextAsync(), Is.True);

        Assert.That(reader.Current.Entries, Is.EqualTo([
                new VolumeChangeEntry(@"X:\repo\file.txt", VolumeEntryKind.Deleted),
            new VolumeChangeEntry(@"X:\repo\renamed.txt", VolumeEntryKind.Created)
        ]));
        Assert.That(await reader.MoveNextAsync(), Is.True);
        Assert.That(reader.Current.Entries.Single(), Is.EqualTo(
            new VolumeChangeEntry(@"X:\repo\renamed.txt", VolumeEntryKind.Deleted)));
        await scripted.WatchFor('Y').PublishBatchAsync([Entry(51, 6, "other.txt", SyntheticJournalReason.FileCreate)], new SyntheticJournalCursor(JournalIdentifier, 1002));
        Assert.That(await reader.MoveNextAsync(), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.Current.Volume, Is.EqualTo("Y"));
            Assert.That(reader.Current.Entries.Single().FullPath, Is.EqualTo(@"Y:\repo\other.txt"));
            Assert.That(owner.Index.Search(new SearchQuery("other.txt", NameMatchMode.Exact)).Single().Parent!.Value.Name, Is.EqualTo("repo"));
        }
    }

    [Test]
    public Task OrdinaryFileModificationHydratesOmittedRow() =>
        AssertHydrationAsync(SyntheticJournalReason.DataOverwrite, VolumeEntryKind.Modified);

    [Test]
    public Task OrdinaryFileDeletionHydratesOmittedRow() =>
        AssertHydrationAsync(SyntheticJournalReason.FileDelete, VolumeEntryKind.Deleted);

    static async Task AssertHydrationAsync(SyntheticJournalReason reason, VolumeEntryKind expected)
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        await File.WriteAllTextAsync(Path.Combine(files.TreePath, "repo", "ordinary.txt"), "omitted by profile");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
        Assert.That(owner.Index.Search(new SearchQuery("ordinary.txt", NameMatchMode.Exact)), Is.Empty);
        var observed = new TaskCompletionSource<FileChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Index.Changed += change => observed.TrySetResult(change);
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await scripted.WaitForStartAsync('X', 1, CancellationToken.None).WaitAsync(TestTimeouts.HangGuard);
        await scripted.WatchFor('X').PublishBatchAsync([Entry(50, 6, "ordinary.txt", reason)], new SyntheticJournalCursor(JournalIdentifier, 1002));
        Assert.That(await pending, Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.Current.Volume, Is.EqualTo("X"));
            // Routed through IndexVolumeChangeSource, which rewrites this back onto its own
            // configured drive letter (MFTLib #143), so this stays the pre-existing synthetic shape.
            Assert.That(reader.Current.Entries.Single(), Is.EqualTo(new VolumeChangeEntry(@"X:\repo\ordinary.txt", expected)));
        }
        var change = await observed.Task.WaitAsync(TestTimeouts.HangGuard);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Entry.Name, Is.EqualTo("ordinary.txt"));
            Assert.That(change.Entry.IsDirectory, Is.False);
            Assert.That(change.Entry.Parent!.Value.Name, Is.EqualTo("repo"));
            Assert.That(change.Entry.IsDeleted, Is.EqualTo(expected == VolumeEntryKind.Deleted));
        }
    }

    [Test]
    public async Task CancellationStopsPumpAndCompletesReader()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        foreach (var disposeSource in new[] { false, true })
        {
            var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
            var owner = Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
            var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
            var deaths = 0;
            source.SourceDied += _ => deaths++;
            try
            {
                await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
                using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
                await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();
                var pending = reader.MoveNextAsync().AsTask();
                await scripted.WaitForStartAsync('X', 1, CancellationToken.None).WaitAsync(TestTimeouts.HangGuard);
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
                Assert.That(await scripted.WatchFor('X').ReadEnded.WaitAsync(TestTimeouts.HangGuard), Is.True);
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
    public async Task FreshOpen_ReportsScanProgressWithDriveOrdinal()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var owner = Owner(await files.OpenIndexAsync(), "X", "Y");
        IProgress<IndexScanProgress>? capturedProgress = null;
        await using var source = new IndexVolumeChangeSource((drives, progress, _) =>
        {
            Assert.That(drives, Is.EqualTo(["X", "Y"]));
            capturedProgress = progress;
            return Task.FromResult(owner);
        });
        var samples = new List<VolumeScanProgress>();
        source.ScanProgress += samples.Add;

        await source.ArmAndCatchUpAsync([@"x:\", "Y:"], CancellationToken.None);

        Assert.That(capturedProgress, Is.Not.Null);
        capturedProgress!.Report(SyntheticNotifications.CreateIndexScanProgress('y', IndexScanPhase.ParsingMft, 600_000, expectedRowCount: 1_200_000));
        Assert.That(samples, Is.EqualTo([
            new VolumeScanProgress("Y", 2, 2, 600_000u, 1_200_000u)
        ]));
    }

    [Test]
    public async Task CacheDeclinedDriveSurfacesCheckpointLossHint()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var owner = new MftIndexSession(await files.OpenIndexAsync(), null, ["X"],
            [new IndexedDrive('X', files.TreePath, 12345u)], new Dictionary<string, string>());
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner), owner);
        var loss = SyntheticNotifications.CreateJournalCheckpointLoss('X', JournalCheckpointLossCause.CheckpointTrimmed, JournalCheckpointLossDetection.DriveOpening, new UsnJournalSettings { MaximumSize = 32L * 1024 * 1024, AllocationDelta = 8 * 1024 * 1024 }) with { BytesBehind = 4_000_000, SizeThatWouldHaveRetained = 136L * 1024 * 1024 };
        var declined = DriveStatuses.CreateDriveStatus('X', DriveState.Failed,
            failure: SyntheticNotifications.CreateDriveFailure(DriveFailureKind.CacheDeclined, "cache declined"));
        var status = declined with { Watch = declined.Watch with { CheckpointLoss = loss } };
        var errors = new Dictionary<string, string>();
        var warnings = new List<JournalWarning>();

        source.ProcessArmedDrive(status, new GitWizardConfiguration(), errors, warnings, CancellationToken.None);

        Assert.That(errors, Does.ContainKey("X").WithValue("cache declined"));
        var warning = warnings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.DriveLetter, Is.EqualTo("X"));
            Assert.That(warning.Loss, Is.SameAs(loss));
            Assert.That(warning.VolumeSerial, Is.EqualTo(12345u));
        }
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
        var ready = DriveStatuses.CreateDriveStatus('X', DriveState.Ready,
            SyntheticNotifications.CreateDriveBlockStatus(BlockOrigin.ProducedByScan, 10, false, DateTime.UtcNow),
            watchSupported: true);
        var status = ready with { Watch = ready.Watch with { FailureMessage = "watch reached a position the journal no longer holds", CheckpointLoss = liveLoss, State = WatchState.Faulted, IsRecoveryStopped = true } };

        source.ProcessFaultedDrive(status, owner, new GitWizardConfiguration());

        var warning = warnings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.DriveLetter, Is.EqualTo("X"));
            Assert.That(warning.Loss, Is.SameAs(liveLoss));
            Assert.That(warning.WatchState, Is.EqualTo(WatchState.Faulted));
            Assert.That(warning.IsRecoveryStopped, Is.True);
            Assert.That(warning.VolumeSerial, Is.EqualTo(12345u));
        }

        // A fault the journal did not cause reports nothing: the open's standing report and a
        // missing report both stay out of the event.
        var openLoss = FakeJournalSettingsSource.TrimmedLoss('X', 32L * 1024 * 1024, 136L * 1024 * 1024);
        source.ProcessFaultedDrive(status with { Watch = status.Watch with { CheckpointLoss = openLoss } }, owner, new GitWizardConfiguration());
        source.ProcessFaultedDrive(status with { Watch = status.Watch with { CheckpointLoss = null } }, owner, new GitWizardConfiguration());
        Assert.That(warnings, Has.Count.EqualTo(1));
    }


    [Test]
    public async Task UnrelatedFaultAfterRecoveredLoss_DoesNotRaiseTheReportedLossAgain()
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
        var recreated = DriveStatuses.CreateDriveStatus('X', DriveState.Ready,
            SyntheticNotifications.CreateDriveBlockStatus(BlockOrigin.ProducedByScan, 10, false, DateTime.UtcNow),
            watchSupported: true);
        var status = recreated with { Watch = recreated.Watch with { CheckpointLoss = liveLoss } };

        source.ProcessFaultedDrive(status, owner, new GitWizardConfiguration());
        // The library keeps the report after its recovery rescan, so a later fault of another
        // kind sees the same report; it explained the earlier episode, not this one.
        source.ProcessFaultedDrive(status, owner, new GitWizardConfiguration());
        Assert.That(warnings, Has.Count.EqualTo(1));

        // The recovery then fails for good: the same report, a newer watch state. The published
        // hint is updated in place and the report is still not raised as a new episode.
        var terminal = status with { Watch = status.Watch with { State = WatchState.Faulted, IsRecoveryStopped = true, StateVersion = status.Watch.StateVersion + 1 } };
        source.ProcessFaultedDrive(terminal, owner, new GitWizardConfiguration());
        source.ProcessFaultedDrive(terminal, owner, new GitWizardConfiguration());
        Assert.Multiple(() =>
        {
            Assert.That(warnings, Has.Count.EqualTo(2));
            Assert.That(warnings[^1].Loss, Is.SameAs(liveLoss));
            Assert.That(warnings[^1].IsRecoveryStopped, Is.True);
            Assert.That(warnings[^1].Message, Does.Contain("Automatic recovery stopped"));
        });

        // Two successive journal recreations produce distinct report instances with identical
        // record fields. ProcessFaultedDrive uses object identity to treat the second as a new episode.
        var recreationLoss2 = liveLoss with { };
        Assert.That(recreationLoss2, Is.EqualTo(liveLoss));
        Assert.That(recreationLoss2, Is.Not.SameAs(liveLoss));

        source.ProcessFaultedDrive(status with { Watch = status.Watch with { CheckpointLoss = recreationLoss2 } }, owner, new GitWizardConfiguration());
        source.ProcessFaultedDrive(status with { Watch = status.Watch with { CheckpointLoss = recreationLoss2 } }, owner, new GitWizardConfiguration());
        Assert.That(warnings.Select(warning => warning.Loss),
            Is.EqualTo([liveLoss, liveLoss, recreationLoss2]));
    }
}
