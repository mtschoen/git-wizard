using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;

namespace GitWizardTests.Watch;

[TestFixture]
public class IndexVolumeChangeSourceTests
{
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
        Assert.That(change.Kind, Is.EqualTo(FileChangeKind.Renamed));
        Assert.That(change.Entry.Path, Is.Not.EqualTo(change.Path));
        Assert.That(change.Path, Is.EqualTo(@"X:\first"));
        Assert.That(change.Entry.Path, Is.EqualTo(@"X:\second"));
        Assert.That(change.PreviousPath, Is.EqualTo(@"X:\repo"));
    }

    [Test]
    public async Task ReusesMatchingPreparedIndex()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var connection = new CountingOwner();
        var owner = new MftIndexSession(await files.OpenIndexAsync(), connection, ["X"],
            new Dictionary<string, string> { ["Z"] = "scan denied" });
        var source = new IndexVolumeChangeSource((_, _) =>
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
            await using (var source = new IndexVolumeChangeSource((drives, cancellationToken) =>
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
        await using var failedSource = new IndexVolumeChangeSource((_, _) => Task.FromResult(failedOwner));
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
        var owner = new MftIndexSession(await files.OpenIndexAsync(), connection, ["X"],
            new Dictionary<string, string>());
        var opens = 0;
        var source = new IndexVolumeChangeSource((_, _) =>
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
        await using var source = new IndexVolumeChangeSource((_, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X", "Y"], CancellationToken.None);
        Assert.That(scripted.Started.IsCompleted, Is.False, "Arming must not start the watch.");
        using var cancellation = new CancellationTokenSource(HangGuard);
        await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        var firstRead = reader.MoveNextAsync().AsTask();
        var targets = await scripted.Started.WaitAsync(HangGuard);
        Assert.That(targets, Is.EquivalentTo(new[]
        {
            new IndexWatchTarget('X', PackedIndexFixture.JournalId, PackedIndexFixture.NextUsn),
            new IndexWatchTarget('Y', PackedIndexFixture.JournalId, PackedIndexFixture.NextUsn)
        }));
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
        await using var source = new IndexVolumeChangeSource((_, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        using var cancellation = new CancellationTokenSource(HangGuard);
        await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await scripted.Started.WaitAsync(HangGuard);
        scripted.Publish(new JournalBatch('X', [Entry(50, 6, "ordinary.txt", reason)],
            PackedIndexFixture.JournalId, 1002));
        Assert.That(await pending, Is.True);
        Assert.That(reader.Current.Volume, Is.EqualTo("X"));
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
            var source = new IndexVolumeChangeSource((_, _) => Task.FromResult(owner));
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
        var source = new IndexVolumeChangeSource((_, _) => Task.FromResult(owner));
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

    static MftIndexSession Owner(FileIndex index, params string[] drives) =>
        new(index, null, drives, new Dictionary<string, string>());

    sealed class CountingOwner : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
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
