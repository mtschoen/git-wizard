using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;

namespace GitWizardTests;

[TestFixture]
public class MftIndexPersistenceTests
{
    static async Task SeedAsync(PackedIndexFixture files, IndexedDrive drive, string cache)
    {
        await using var seed = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [drive],
            CacheDirectory = cache,
            NoCache = false,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = files.ProduceMftAsync
        }, CancellationToken.None);
        Assert.That(seed.Drives.Single().BlockSource, Is.EqualTo(BlockSource.ProducedByScan));
    }

    // The owning index keeps its block mapped for writing, so a reader must share write access.
    static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes);
        return bytes;
    }

    [TestCase(MftIndexOpenIntent.Persistent)]
    [TestCase(MftIndexOpenIntent.Snapshot)]
    public async Task WarmOpenDoesNotLaunchBroker(MftIndexOpenIntent intent)
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        var drive = new IndexedDrive('X', files.TreePath, 1);
        var cache = MftIndexCachePolicy.Current.GetCacheDirectory(files.DirectoryPath);
        await SeedAsync(files, drive, cache);
        var calls = 0;
        Task<JournalBrokerClient> Unexpected(CancellationToken cancellationToken)
        {
            calls++;
            throw new AssertionException("A warm open must not connect.");
        }
        await using var session = await MftIndexSession.OpenAsync([drive], Unexpected,
            cache, intent, null, CancellationToken.None);
        Assert.That(calls, Is.Zero);
        Assert.That(session.CaptureDriveStatuses().Single().IndexStatus!.BlockSource,
            Is.EqualTo(BlockSource.WarmStartedFromCache));
        Assert.That(session.Index.FindByName(".git"), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SnapshotMissIsTypedRefusalWithoutConnection()
    {
        await using var broker = new BlockBrokerFixture();
        await using var session = await MftIndexSession.OpenAsync([broker.Drive('X')], broker.ConnectAsync,
            MftIndexCachePolicy.Current.GetCacheDirectory(broker.CacheDirectory),
            MftIndexOpenIntent.Snapshot, null, CancellationToken.None);
        var status = session.CaptureDriveStatuses().Single().IndexStatus!;
        Assert.That(status.State, Is.EqualTo(DriveState.Failed));
        Assert.That(status.FailureKind, Is.EqualTo(DriveFailureKind.CacheDeclined));
        Assert.That(session.ScanErrors, Contains.Key("X"));
        Assert.That(broker.ConnectionCount, Is.Zero);
        Assert.That(broker.ScanFrames, Is.Empty);
    }

    [Test]
    public async Task SnapshotInUseDoesNotScanAndNormalCompetitorIsPrivate()
    {
        await using var firstBroker = new BlockBrokerFixture();
        await using var otherBroker = new BlockBrokerFixture();
        var drive = firstBroker.Drive('X');
        var cache = MftIndexCachePolicy.Current.GetCacheDirectory(firstBroker.CacheDirectory);
        // Disposed mid-test to release the canonical slot; the finally is an idempotent safety net.
        var first = await MftIndexSession.OpenAsync([drive], firstBroker.ConnectAsync,
            cache, MftIndexOpenIntent.Persistent, null, CancellationToken.None);
        try
        {
            await AssertInUseAndPrivateCompetitorAsync(first, firstBroker, otherBroker, drive, cache);
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    static async Task AssertInUseAndPrivateCompetitorAsync(MftIndexSession first, BlockBrokerFixture firstBroker,
        BlockBrokerFixture otherBroker, IndexedDrive drive, string cache)
    {
        var canonical = firstBroker.SectionOptions.Single().Path;
        var before = await ReadSharedAsync(canonical);
        await using (var refused = await MftIndexSession.OpenAsync([drive], otherBroker.ConnectAsync,
            cache, MftIndexOpenIntent.Snapshot, null, CancellationToken.None))
        {
            Assert.That(refused.CaptureDriveStatuses().Single().IndexStatus!.FailureKind,
                Is.EqualTo(DriveFailureKind.InUse));
            Assert.That(otherBroker.ConnectionCount, Is.Zero);
        }
        string privatePath;
        await using (var competitor = await MftIndexSession.OpenAsync([drive], otherBroker.ConnectAsync,
            cache, MftIndexOpenIntent.Persistent, null, CancellationToken.None))
        {
            var options = otherBroker.SectionOptions.Single();
            privatePath = options.Path;
            Assert.That(options.DeleteOnClose, Is.True);
            Assert.That(privatePath, Is.Not.EqualTo(canonical));
            Assert.That(competitor.Index.FindByName(".git"), Has.Count.EqualTo(1));
            Assert.That(await ReadSharedAsync(canonical), Is.EqualTo(before));
        }
        Assert.That(File.Exists(privatePath), Is.False);
        await first.DisposeAsync();
        Assert.That(await ReadSharedAsync(canonical), Is.EqualTo(before));
        await using var reopened = await MftIndexSession.OpenAsync([drive],
            _ => throw new AssertionException("Canonical cache must remain reusable."), cache,
            MftIndexOpenIntent.Snapshot, null, CancellationToken.None);
        Assert.That(reopened.CaptureDriveStatuses().Single().IndexStatus!.BlockSource,
            Is.EqualTo(BlockSource.WarmStartedFromCache));
    }

    [Test]
    public async Task WarmBlockReplaysAfterSavedCheckpoint()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        var drive = new IndexedDrive('X', files.TreePath, 1);
        var cache = MftIndexCachePolicy.Current.GetCacheDirectory(files.DirectoryPath);
        await SeedAsync(files, drive, cache);
        var source = new ScriptedIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [drive],
            CacheDirectory = cache,
            NoCache = false,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = (_, _) => throw new AssertionException("Warm open must not produce."),
            WatchSource = source
        }, CancellationToken.None);
        Assert.That(index.Drives.Single().BlockSource, Is.EqualTo(BlockSource.WarmStartedFromCache));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.Changed += _ => changed.TrySetResult();
        await index.StartWatchingAsync(CancellationToken.None);
        var targets = await source.Started.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(targets.Single().JournalId, Is.EqualTo(PackedIndexFixture.JournalId));
        Assert.That(targets.Single().NextUsn, Is.EqualTo(PackedIndexFixture.NextUsn));
        source.Publish(new JournalBatch('X',
        [
            UsnJournalEntry.Create(new UsnJournalEntryOptions
            {
                RecordNumber = 6,
                ParentRecordNumber = 5,
                SequenceNumber = 1,
                FileName = "renamed",
                Reason = UsnReason.RenameNewName,
                FileAttributes = FileAttributes.Directory,
                Timestamp = PackedIndexFixture.CompletionTimestamp,
                Usn = PackedIndexFixture.NextUsn + 1
            })
        ], PackedIndexFixture.JournalId, PackedIndexFixture.NextUsn + 1));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(index.FindByName("renamed"), Has.Count.EqualTo(1));
        await index.StopWatchingAsync(CancellationToken.None);
    }

    [Test]
    public async Task WarmOpenThenFirstWatchClosesLateClient()
    {
        using var files = new PackedIndexFixture();
        await using var broker = new BlockBrokerFixture();
        var drive = broker.Drive('X');
        var cache = MftIndexCachePolicy.Current.GetCacheDirectory(files.DirectoryPath);
        await SeedAsync(files, drive, cache);
        // The fixture treats a cancelled connect token as an abandoned client; the watch's own token is
        // cancelled when disposal stops watching, which would hide the orderly shutdown frame under test.
        var session = await MftIndexSession.OpenAsync([drive], _ => broker.ConnectAsync(CancellationToken.None),
            cache, MftIndexOpenIntent.Snapshot, null, CancellationToken.None);
        try
        {
            Assert.That(broker.ConnectionCount, Is.Zero);
            await session.Index.StartWatchingAsync(CancellationToken.None);
            await broker.WatchStarted.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(broker.ConnectionCount, Is.EqualTo(1));
            Assert.That(broker.ScanFrames, Is.Empty);
        }
        finally
        {
            await session.DisposeAsync();
        }
        await session.DisposeAsync();
        await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(broker.ShutdownFrames, Is.EqualTo(1));
        Assert.That(broker.ClientTransportDisposed, Is.True);
    }

    [Test]
    public async Task SnapshotRefusalCanExplicitlyRescanThroughLazyConnection()
    {
        await using var broker = new BlockBrokerFixture();
        var cache = MftIndexCachePolicy.Current.GetCacheDirectory(broker.CacheDirectory);
        var session = await MftIndexSession.OpenAsync([broker.Drive('X')], broker.ConnectAsync,
            cache, MftIndexOpenIntent.Snapshot, null, CancellationToken.None);
        try
        {
            var old = session.CaptureDriveStatuses();
            Assert.That(old.Single().IndexStatus!.FailureKind, Is.EqualTo(DriveFailureKind.CacheDeclined));
            Assert.That(broker.ConnectionCount, Is.Zero);
            await session.Index.RescanAsync('X', CancellationToken.None);
            var current = session.CaptureDriveStatuses().Single().IndexStatus!;
            Assert.That(current.BlockSource, Is.EqualTo(BlockSource.ProducedByScan));
            Assert.That(current.FailureKind, Is.EqualTo(DriveFailureKind.None));
            Assert.That(current.MftProducerFailureMessage, Is.Null);
            Assert.That(old.Single().IndexStatus!.FailureKind, Is.EqualTo(DriveFailureKind.CacheDeclined));
            Assert.That(broker.ConnectionCount, Is.EqualTo(1));
        }
        finally
        {
            await session.DisposeAsync();
        }
        await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(broker.ShutdownFrames, Is.EqualTo(1));
    }

    [Test]
    public async Task SnapshotOfflineVolumeRemainsVisibleWithoutConnecting()
    {
        using var files = new PackedIndexFixture();
        await using var broker = new BlockBrokerFixture();
        await using var session = await MftIndexSession.OpenAsync(
            [new IndexedDrive('X', Path.Combine(files.DirectoryPath, "absent"), 1)],
            broker.ConnectAsync, MftIndexCachePolicy.Current.GetCacheDirectory(files.DirectoryPath),
            MftIndexOpenIntent.Snapshot, null, CancellationToken.None);
        var status = session.CaptureDriveStatuses().Single().IndexStatus!;
        Assert.That(status.State, Is.EqualTo(DriveState.Offline));
        Assert.That(status.FailureKind, Is.EqualTo(DriveFailureKind.None));
        Assert.That(session.ScanErrors, Contains.Key("X"));
        Assert.That(broker.ConnectionCount, Is.Zero);
    }
}
