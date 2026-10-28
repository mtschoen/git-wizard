using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests;

[TestFixture]
public class MftIndexPersistenceTests
{
    static async Task SeedAsync(PackedIndexFixture files, RootKey drive, string cache)
    {
        await using var seed = await FileIndex.OpenAsync(files.ConfigureOptions(new FileIndexOptions
        {
            Root = drive,
            CacheDirectory = cache,
            CacheTag = MftIndexCachePolicy.CacheTag,
            MftSource = SyntheticIndexSource.Create(files.Producer)
        }), CancellationToken.None);
        Assert.That(seed.Status.Block?.Origin, Is.EqualTo(BlockOrigin.ProducedByScan));
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

    [TestCase(CacheMode.Persistent)]
    [TestCase(CacheMode.ExistingOnly)]
    public async Task WarmOpenDoesNotLaunchBroker(CacheMode intent)
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        var drive = (RootKey)new VolumeKey('X');
        var cache = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        await SeedAsync(files, drive, cache);
        var calls = 0;
        await using var session = await MftIndexSession.OpenAsync([drive], BrokerTestHarness.CreateSession(Unexpected),
            cache, intent, null, CancellationToken.None, configureOptions: files.ConfigureOptions);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.Zero);
            Assert.That(session.CaptureRootStatuses().Single().IndexStatus!.Block?.Origin,
                Is.EqualTo(BlockOrigin.WarmStartedFromCache));
            Assert.That(session.IndexSet.Search(new SearchQuery(".git", NameMatchMode.Exact)), Has.Count.EqualTo(1));
        }
        return;

        Task<InProcessBrokerHandle> Unexpected(CancellationToken _)
        {
            calls++;
            throw new AssertionException("A warm open must not connect.");
        }
    }

    [Test]
    public async Task SnapshotMissIsTypedRefusalWithoutConnection()
    {
        await using var broker = new BlockBrokerFixture();
        await using var session = await MftIndexSession.OpenAsync([broker.Drive('X')], broker.CreateSession(),
            MftIndexCachePolicy.GetCacheDirectory(broker.CacheDirectory),
            CacheMode.ExistingOnly, null, CancellationToken.None, configureOptions: broker.ConfigureOptions);
        var status = session.CaptureRootStatuses().Single().IndexStatus!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.State, Is.EqualTo(RootState.Failed));
            Assert.That(status.Failure?.Kind, Is.EqualTo(RootFailureKind.CacheDeclined));
            Assert.That(status.Block, Is.Null);
            Assert.That(session.ScanErrors, Contains.Key("X"));
            Assert.That(broker.ConnectionCount, Is.Zero);
            Assert.That(broker.ScannedDrives, Is.Empty);
        }
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
            cache, CacheMode.Persistent, null, CancellationToken.None, configureOptions: firstBroker.ConfigureOptions);
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
        BlockBrokerFixture otherBroker, RootKey drive, string cache)
    {
        var canonical = SyntheticBlock.CachedPath(cache, new VolumeKey('X'), 1);
        var before = await ReadSharedAsync(canonical);
        await using (var refused = await MftIndexSession.OpenAsync([drive], otherBroker.CreateSession(),
            cache, CacheMode.ExistingOnly, null, CancellationToken.None, configureOptions: otherBroker.ConfigureOptions))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(refused.CaptureRootStatuses().Single().IndexStatus!.Failure?.Kind,
                            Is.EqualTo(RootFailureKind.InUse));
                Assert.That(otherBroker.ConnectionCount, Is.Zero);
            }
        }
        await using (var competitor = await MftIndexSession.OpenAsync([drive], otherBroker.CreateSession(),
            cache, CacheMode.Persistent, null, CancellationToken.None, configureOptions: otherBroker.ConfigureOptions))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(competitor.CaptureRootStatuses().Single().IndexStatus!.Block?.CacheLocation,
                            Is.EqualTo(CacheLocation.PrivateFallback));
                Assert.That(competitor.IndexSet.Search(new SearchQuery(".git", NameMatchMode.Exact)), Has.Count.EqualTo(1));
                Assert.That(await ReadSharedAsync(canonical), Is.EqualTo(before));
            }
        }
        await first.DisposeAsync();
        Assert.That(await ReadSharedAsync(canonical), Is.EqualTo(before));
        await using var reopened = await MftIndexSession.OpenAsync([drive],
            BrokerTestHarness.CreateSession(_ => throw new AssertionException("Canonical cache must remain reusable.")),
            cache, CacheMode.ExistingOnly, null, CancellationToken.None, configureOptions: otherBroker.ConfigureOptions);
        Assert.That(reopened.CaptureRootStatuses().Single().IndexStatus!.Block?.Origin,
            Is.EqualTo(BlockOrigin.WarmStartedFromCache));
    }

    [Test]
    public async Task WarmBlockReplaysAfterSavedCheckpoint()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        var drive = (RootKey)new VolumeKey('X');
        var cache = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        await SeedAsync(files, drive, cache);
        var source = new ScriptedWatchSource { CatchUpOnStart = true };
        await using var index = await FileIndex.OpenAsync(MFTLibTestExtensions.SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = drive,
            CacheDirectory = cache,
            CacheTag = MftIndexCachePolicy.CacheTag,
            MftSource = SyntheticIndexSource.Create(
                new SyntheticMftProducer(_ => throw new AssertionException("Warm open must not produce.")), source)
        }, 1, volume: new VolumeKey('X'), volumeRootDirectory: files.TreePath), CancellationToken.None);
        Assert.That(index.Status.Block?.Origin, Is.EqualTo(BlockOrigin.WarmStartedFromCache));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.Changed += _ => changed.TrySetResult();
        await index.StartWatchingAsync(CancellationToken.None);
        var start = source.Starts.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(start.Cursor.JournalIdentifier, Is.EqualTo(PackedIndexFixture.JournalIdentifier));
            Assert.That(start.Cursor.NextUsn, Is.EqualTo(PackedIndexFixture.NextUsn));
        }
        await source.WatchFor('X').PublishBatchAsync([
            new SyntheticJournalRecord
            {
                RecordNumber = 6,                ParentRecordNumber = 5,                SequenceNumber = 1,                FileName = "renamed",                Reason = SyntheticJournalReason.RenameNewName,                FileAttributes = FileAttributes.Directory,                Timestamp = PackedIndexFixture.CompletionTimestamp,                Usn = PackedIndexFixture.NextUsn + 1
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
            cache, CacheMode.ExistingOnly, null, CancellationToken.None, configureOptions: broker.ConfigureOptions);
        try
        {
            Assert.That(broker.ConnectionCount, Is.Zero);
            await session.Indices.Single().StartWatchingAsync(CancellationToken.None);
            await broker.WatchStarted.WaitAsync(TimeSpan.FromSeconds(10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(broker.ConnectionCount, Is.EqualTo(1));
                Assert.That(broker.ScannedDrives, Is.Empty);
            }
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
            cache, CacheMode.ExistingOnly, null, CancellationToken.None, configureOptions: broker.ConfigureOptions);
        try
        {
            var old = session.CaptureRootStatuses();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(old.Single().IndexStatus!.Failure?.Kind, Is.EqualTo(RootFailureKind.CacheDeclined));
                Assert.That(broker.ConnectionCount, Is.Zero);
            }
            await session.Indices.Single().RescanAsync(CancellationToken.None);
            var current = session.CaptureRootStatuses().Single().IndexStatus!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(current.Block?.Origin, Is.EqualTo(BlockOrigin.ProducedByScan));
                Assert.That(current.Failure, Is.Null);
                Assert.That(old.Single().IndexStatus!.Failure?.Kind, Is.EqualTo(RootFailureKind.CacheDeclined));
                Assert.That(broker.ConnectionCount, Is.EqualTo(1));
            }
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
            [(RootKey)new VolumeKey('X')],
            broker.CreateSession(), MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath),
            CacheMode.ExistingOnly, null, CancellationToken.None, configureOptions: options =>
                SyntheticVolumeFacts.Apply(options, 1, volume: new VolumeKey('X'),
                    volumeRootDirectory: Path.Combine(files.DirectoryPath, "absent")));
        var status = session.CaptureRootStatuses().Single().IndexStatus!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.State, Is.EqualTo(RootState.Offline));
            Assert.That(status.Failure, Is.Null);
            Assert.That(status.Block, Is.Null);
            Assert.That(session.ScanErrors, Contains.Key("X"));
            Assert.That(broker.ConnectionCount, Is.Zero);
        }
    }
}
