using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

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
            CacheTag = MftIndexCachePolicy.CacheTag,
            NoCache = false,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = SyntheticIndexSource.Create(files.Producer)
        }, CancellationToken.None);
        Assert.That(seed.Drives.Single().Block.Source, Is.EqualTo(BlockSource.ProducedByScan));
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
        var cache = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        await SeedAsync(files, drive, cache);
        var calls = 0;
        Task<InProcessBrokerHandle> Unexpected(CancellationToken cancellationToken)
        {
            calls++;
            throw new AssertionException("A warm open must not connect.");
        }
        await using var session = await MftIndexSession.OpenAsync([drive], BrokerTestHarness.CreateSession(Unexpected),
            cache, intent, null, CancellationToken.None);
        Assert.That(calls, Is.Zero);
        Assert.That(session.CaptureDriveStatuses().Single().IndexStatus!.Block.Source,
            Is.EqualTo(BlockSource.WarmStartedFromCache));
        Assert.That(session.Index.Search(new SearchQuery(".git", NameMatchMode.Exact)), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SnapshotMissIsTypedRefusalWithoutConnection()
    {
        await using var broker = new BlockBrokerFixture();
        await using var session = await MftIndexSession.OpenAsync([broker.Drive('X')], broker.CreateSession(),
            MftIndexCachePolicy.GetCacheDirectory(broker.CacheDirectory),
            MftIndexOpenIntent.Snapshot, null, CancellationToken.None);
        var status = session.CaptureDriveStatuses().Single().IndexStatus!;
        Assert.That(status.State, Is.EqualTo(DriveState.Failed));
        Assert.That(status.FailureKind, Is.EqualTo(DriveFailureKind.CacheDeclined));
        Assert.That(session.ScanErrors, Contains.Key("X"));
        Assert.That(broker.ConnectionCount, Is.Zero);
        Assert.That(broker.ScannedDrives, Is.Empty);
    }

    [Test]
    public async Task SnapshotInUseDoesNotScanAndNormalCompetitorIsPrivate()
    {
        await using var firstBroker = new BlockBrokerFixture();
        await using var otherBroker = new BlockBrokerFixture();
        var drive = firstBroker.Drive('X');
        var cache = MftIndexCachePolicy.GetCacheDirectory(firstBroker.CacheDirectory);
        // Disposed mid-test to release the canonical slot; the finally is an idempotent safety net.
        var first = await MftIndexSession.OpenAsync([drive], firstBroker.CreateSession(),
            cache, MftIndexOpenIntent.Persistent, null, CancellationToken.None);
        try
        {
            await AssertInUseAndPrivateCompetitorAsync(first, otherBroker, drive, cache);
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    static async Task AssertInUseAndPrivateCompetitorAsync(MftIndexSession first,
        BlockBrokerFixture otherBroker, IndexedDrive drive, string cache)
    {
        var canonical = SyntheticBlock.CachedPath(cache, 'X', 1);
        var before = await ReadSharedAsync(canonical);
        await using (var refused = await MftIndexSession.OpenAsync([drive], otherBroker.CreateSession(),
            cache, MftIndexOpenIntent.Snapshot, null, CancellationToken.None))
        {
            Assert.That(refused.CaptureDriveStatuses().Single().IndexStatus!.FailureKind,
                Is.EqualTo(DriveFailureKind.InUse));
            Assert.That(otherBroker.ConnectionCount, Is.Zero);
        }
        await using (var competitor = await MftIndexSession.OpenAsync([drive], otherBroker.CreateSession(),
            cache, MftIndexOpenIntent.Persistent, null, CancellationToken.None))
        {
            Assert.That(competitor.CaptureDriveStatuses().Single().IndexStatus!.Block.CacheSlot,
                Is.EqualTo(CacheSlotState.PrivateFallback));
            Assert.That(competitor.Index.Search(new SearchQuery(".git", NameMatchMode.Exact)), Has.Count.EqualTo(1));
            Assert.That(await ReadSharedAsync(canonical), Is.EqualTo(before));
        }
        await first.DisposeAsync();
        Assert.That(await ReadSharedAsync(canonical), Is.EqualTo(before));
        await using var reopened = await MftIndexSession.OpenAsync([drive],
            BrokerTestHarness.CreateSession(_ => throw new AssertionException("Canonical cache must remain reusable.")),
            cache, MftIndexOpenIntent.Snapshot, null, CancellationToken.None);
        Assert.That(reopened.CaptureDriveStatuses().Single().IndexStatus!.Block.Source,
            Is.EqualTo(BlockSource.WarmStartedFromCache));
    }

    [Test]
    public async Task WarmBlockReplaysAfterSavedCheckpoint()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        var drive = new IndexedDrive('X', files.TreePath, 1);
        var cache = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        await SeedAsync(files, drive, cache);
        var source = new ScriptedWatchSource { CatchUpOnStart = true };
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [drive],
            CacheDirectory = cache,
            CacheTag = MftIndexCachePolicy.CacheTag,
            NoCache = false,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = SyntheticIndexSource.Create(
                new SyntheticMftProducer(_ => throw new AssertionException("Warm open must not produce.")), source)
        }, CancellationToken.None);
        Assert.That(index.Drives.Single().Block.Source, Is.EqualTo(BlockSource.WarmStartedFromCache));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.Changed += _ => changed.TrySetResult();
        await index.StartWatchingAsync(CancellationToken.None);
        var start = source.Starts.Single();
        Assert.That(start.Cursor.JournalIdentifier, Is.EqualTo(PackedIndexFixture.JournalIdentifier));
        Assert.That(start.Cursor.NextUsn, Is.EqualTo(PackedIndexFixture.NextUsn));
        await source.WatchFor('X').PublishBatchAsync([
            new SyntheticJournalRecord
            {
                RecordNumber = 6,
                ParentRecordNumber = 5,
                SequenceNumber = 1,
                FileName = "renamed",
                Reason = SyntheticJournalReason.RenameNewName,
                FileAttributes = FileAttributes.Directory,
                Timestamp = PackedIndexFixture.CompletionTimestamp,
                UpdateSequenceNumber = PackedIndexFixture.NextUsn + 1
            }
        ], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, PackedIndexFixture.NextUsn + 1));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(index.Search(new SearchQuery("renamed", NameMatchMode.Exact)), Has.Count.EqualTo(1));
        await index.StopWatchingAsync(CancellationToken.None);
    }

    [Test]
    public async Task WarmOpenThenFirstWatchClosesLateClient()
    {
        using var files = new PackedIndexFixture();
        await using var broker = new BlockBrokerFixture();
        var drive = broker.Drive('X');
        var cache = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        await SeedAsync(files, drive, cache);
        var session = await MftIndexSession.OpenAsync([drive], broker.CreateSession(),
            cache, MftIndexOpenIntent.Snapshot, null, CancellationToken.None);
        try
        {
            Assert.That(broker.ConnectionCount, Is.Zero);
            await session.Index.StartWatchingAsync(CancellationToken.None);
            await broker.WatchStarted.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(broker.ConnectionCount, Is.EqualTo(1));
            Assert.That(broker.ScannedDrives, Is.Empty);
        }
        finally
        {
            await session.DisposeAsync();
        }
        await session.DisposeAsync();
        await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task SnapshotRefusalCanExplicitlyRescanThroughLazyConnection()
    {
        await using var broker = new BlockBrokerFixture();
        var cache = MftIndexCachePolicy.GetCacheDirectory(broker.CacheDirectory);
        var session = await MftIndexSession.OpenAsync([broker.Drive('X')], broker.CreateSession(),
            cache, MftIndexOpenIntent.Snapshot, null, CancellationToken.None);
        try
        {
            var old = session.CaptureDriveStatuses();
            Assert.That(old.Single().IndexStatus!.FailureKind, Is.EqualTo(DriveFailureKind.CacheDeclined));
            Assert.That(broker.ConnectionCount, Is.Zero);
            await session.Index.RescanAsync('X', CancellationToken.None);
            var current = session.CaptureDriveStatuses().Single().IndexStatus!;
            Assert.That(current.Block.Source, Is.EqualTo(BlockSource.ProducedByScan));
            Assert.That(current.FailureKind, Is.EqualTo(DriveFailureKind.None));
            Assert.That(current.FailureMessage, Is.Null);
            Assert.That(old.Single().IndexStatus!.FailureKind, Is.EqualTo(DriveFailureKind.CacheDeclined));
            Assert.That(broker.ConnectionCount, Is.EqualTo(1));
        }
        finally
        {
            await session.DisposeAsync();
        }
        await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task SnapshotOfflineVolumeRemainsVisibleWithoutConnecting()
    {
        using var files = new PackedIndexFixture();
        await using var broker = new BlockBrokerFixture();
        await using var session = await MftIndexSession.OpenAsync(
            [new IndexedDrive('X', Path.Combine(files.DirectoryPath, "absent"), 1)],
            broker.CreateSession(), MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath),
            MftIndexOpenIntent.Snapshot, null, CancellationToken.None);
        var status = session.CaptureDriveStatuses().Single().IndexStatus!;
        Assert.That(status.State, Is.EqualTo(DriveState.Offline));
        Assert.That(status.FailureKind, Is.EqualTo(DriveFailureKind.None));
        Assert.That(session.ScanErrors, Contains.Key("X"));
        Assert.That(broker.ConnectionCount, Is.Zero);
    }
}
