using System.Collections.Concurrent;
using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests;

[TestFixture]
public class MftIndexSessionTests
{
    [Test]
    public async Task ConnectsOnceAcrossDrives()
    {
        await using var broker = new BlockBrokerFixture();
        await using (var empty = await MftIndexSession.OpenAsync([], broker.CreateSession(),
            broker.CacheDirectory, null, CancellationToken.None))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(empty.Drives, Is.Empty);
                Assert.That(broker.ConnectionCount, Is.Zero);
            }
        }
        await using var session = await MftIndexSession.OpenAsync([broker.Drive('X'), broker.Drive('Y')],
            broker.CreateSession(), broker.CacheDirectory, null, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(broker.ConnectionCount, Is.EqualTo(1));
            Assert.That(session.Drives, Is.EqualTo(["X", "Y"]));
            Assert.That(session.Index.Drives.Select(drive => drive.State), Is.All.EqualTo(DriveState.Ready));
            Assert.That(broker.ScannedDrives, Is.EquivalentTo(["X", "Y"]));
        }
    }

    [Test]
    public async Task DiscoveryAndWatchShareClient()
    {
        await using var broker = new BlockBrokerFixture();
        var brokerSession = broker.CreateSession();
        var session = await MftIndexSession.OpenAsync([broker.Drive('X')],
            brokerSession, broker.CacheDirectory, null, CancellationToken.None);
        try
        {
            await session.Index.StartWatchingAsync(['X'], CancellationToken.None);
            var watchedDrive = await broker.WatchStarted.WaitAsync(TimeSpan.FromSeconds(10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(watchedDrive.Drive, Is.EqualTo("X"));
                Assert.That(broker.ConnectionCount, Is.EqualTo(1));
                Assert.That(session.IsFaulted, Is.False);
            }
            var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Index.WatchFaulted += _ => fault.TrySetResult();
            await broker.DisconnectAsync();
            await fault.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // WatchFaulted can fire before the broker session records its end, which IsFaulted reads.
            await brokerSession.Ended.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(session.IsFaulted, Is.True);
        }
        finally
        {
            await session.DisposeAsync();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.IsFaulted, Is.True);
            Assert.That(session.IsDisposed, Is.True);
        }
    }

    [Test]
    public async Task DisposeClosesIndexThenClient()
    {
        using var files = new PackedIndexFixture();
        var index = await files.OpenIndexAsync();
        var lifetime = new CountingLifetime(() =>
            Assert.Throws<ObjectDisposedException>(() => index.Search(new SearchQuery(".git", NameMatchMode.Exact))));
        var session = new MftIndexSession(index, lifetime, ["X"], [], new Dictionary<string, string>());
        var first = session.DisposeAsync().AsTask();
        await lifetime.Started.Task;
        var second = session.DisposeAsync().AsTask();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.IsDisposed, Is.True);
            Assert.That(second.IsCompleted, Is.False);
        }
        lifetime.Release.SetResult();
        await Task.WhenAll(first, second);
        await session.DisposeAsync();
        Assert.That(lifetime.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task CancellationClosesClient()
    {
        await using var broker = new BlockBrokerFixture();
        using var cancellation = new CancellationTokenSource();
        var midWriteReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The host's scan holds until the cancelled open closes its channel, so the host always
        // observes the cancellation after exactly one row.
        broker.MidScanAsync = async hostToken =>
        {
            midWriteReached.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, hostToken).ContinueWith(_ => { },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        };
        var drive = broker.Drive('X');
        var cancellationToken = cancellation.Token;
        var cacheDirectory = broker.CacheDirectory;
        var openTask = MftIndexSession.OpenAsync([drive], broker.CreateSession(),
            cacheDirectory, null, cancellationToken);
        try
        {
            var reached = await Task.WhenAny(midWriteReached.Task, openTask).WaitAsync(TimeSpan.FromSeconds(10));
            if (reached == openTask)
            {
                await using var earlySession = await openTask;
                Assert.Fail("Opening completed before reaching the mid-scan gate.");
            }
        }
        finally
        {
            await cancellation.CancelAsync();
        }
        Assert.That(async () => await openTask.WaitAsync(TimeSpan.FromSeconds(10)),
            Throws.InstanceOf<OperationCanceledException>());
        await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(broker.ConnectionCount, Is.EqualTo(1));
            Assert.That(broker.RecordsYielded, Is.EqualTo(1));
            Assert.That(broker.ScanCompleted, Is.False);
        }

        await using var watchBroker = new BlockBrokerFixture();
        using var watchCancellation = new CancellationTokenSource();
        var session = await MftIndexSession.OpenAsync([watchBroker.Drive('X')],
            watchBroker.CreateSession(), watchBroker.CacheDirectory, null, CancellationToken.None);
        await session.Index.StartWatchingAsync(['X'], watchCancellation.Token);
        await watchBroker.WatchStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var cancelling = watchCancellation.CancelAsync();
        var firstDisposal = session.DisposeAsync().AsTask();
        var secondDisposal = session.DisposeAsync().AsTask();
        await Task.WhenAll(cancelling, firstDisposal, secondDisposal);
        await watchBroker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(session.IsDisposed, Is.True);
        Assert.Throws<ObjectDisposedException>(() => session.Index.Search(new SearchQuery(".git", NameMatchMode.Exact)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.IsFaulted, Is.False, "Orderly disposal does not complete the broker end signal.");
            Assert.That(watchBroker.ConnectionCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task CancellationOnScanReceivedClosesClient()
    {
        await using var broker = new BlockBrokerFixture();
        using var cancellation = new CancellationTokenSource();
        broker.ScanReceived = cancellation.Cancel;
        var drive = broker.Drive('X');
        var cancellationToken = cancellation.Token;
        var cacheDirectory = broker.CacheDirectory;
        var brokerSession = broker.CreateSession();
        Assert.That(async () =>
            await MftIndexSession.OpenAsync([drive], brokerSession,
                cacheDirectory, null, cancellationToken), Throws.InstanceOf<OperationCanceledException>());
        await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(broker.ConnectionCount, Is.EqualTo(1));
    }

    [TestCase("w:/")]
    [TestCase(" w/ ", TestName = "Issue370_Session_AcceptsOptionalColonDriveSpec")]
    public async Task OneDriveFailureKeepsOtherDrive(string identityFailureSpec)
    {
        await using var broker = new BlockBrokerFixture(scanError: "volume denied") { FailedDrive = "X" };
        await using var session = await MftIndexSession.OpenSelectedVolumesAsync(
            [identityFailureSpec, "X:", "x:\\", "Y", @"\\server\share", "C:not-root", ""],
            drive => drive == 'W' ? throw new IOException("identity denied") : broker.Drive(drive.Letter),
            broker.CreateSession(), broker.CacheDirectory, null, CancellationToken.None);
        Assert.That(session.Drives, Is.EqualTo(["W", "X", "Y"]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.ScanErrors["W"], Does.Contain("identity denied"));
            Assert.That(session.ScanErrors["X"], Does.Contain("volume denied"));
            Assert.That(session.ScanErrors.ContainsKey("Y"), Is.False);
            Assert.That(session.Index.Drives.Single(drive => drive.Drive == 'Y').State, Is.EqualTo(DriveState.Ready));
            Assert.That(broker.ConnectionCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task AllDrivesFailDoesNotLeak()
    {
        await using (var broker = new BlockBrokerFixture(scanError: "volume denied"))
        {
            var session = await MftIndexSession.OpenAsync([broker.Drive('X'), broker.Drive('Y')],
                broker.CreateSession(), broker.CacheDirectory, null, CancellationToken.None);
            Assert.That(session.ScanErrors.Keys, Is.EquivalentTo(["X", "Y"]));
            await session.DisposeAsync();
            await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await using (var broker = new BlockBrokerFixture { DisconnectOnScan = true })
        {
            await using var session = await MftIndexSession.OpenAsync([broker.Drive('X')],
                broker.CreateSession(), broker.CacheDirectory, null, CancellationToken.None);
            // The drive channels and the control pipe read EOF independently, so the open can return
            // before the process end is observed; IsFaulted is defined once the end has completed.
            await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(session.IsFaulted, Is.True, "Broker death during opening must remain latched.");
                Assert.That(session.ScanErrors, Has.Count.EqualTo(1));
            }
        }
        await using (var broker = new BlockBrokerFixture())
        {
            await using var session = await MftIndexSession.OpenSelectedVolumesAsync(["X", "Y"],
                _ => throw new IOException("identity denied"), broker.CreateSession(),
                broker.CacheDirectory, null, CancellationToken.None);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(session.ScanErrors, Has.Count.EqualTo(2));
                Assert.That(broker.ConnectionCount, Is.Zero);
            }
        }
    }

    [Test]
    public async Task UsesDirectoryScanAndMarkerRetention()
    {
        await using var broker = new BlockBrokerFixture();
        broker.AdditionalScanRecords =
        [
            new SyntheticScanRecord { RecordNumber = 44, ParentRecordNumber = 42, FileName = ".git" },
            new SyntheticScanRecord { RecordNumber = 45, ParentRecordNumber = 42, FileName = "HEAD" },
            new SyntheticScanRecord { RecordNumber = 46, ParentRecordNumber = 42, FileName = "ordinary.txt" }
        ];
        await using var session = await MftIndexSession.OpenAsync([broker.Drive('X')],
            broker.CreateSession(), broker.CacheDirectory, null, CancellationToken.None);
        var scan = broker.Scans.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scan.RetainedFileNames, Is.EqualTo([".git", "HEAD"]));
            Assert.That(session.Index.Search(new SearchQuery(".git", NameMatchMode.Exact)), Has.Count.EqualTo(2));
            Assert.That(session.Index.Search(new SearchQuery("HEAD", NameMatchMode.Exact)).Single().IsDirectory, Is.False);
            Assert.That(session.Index.Search(new SearchQuery("ordinary.txt", NameMatchMode.Exact)), Is.Empty);
            Assert.That(session.Index.Search(new SearchQuery("repo", NameMatchMode.Exact)).Single().IsDirectory, Is.True);
            Assert.That(session.Index.Root('X').RecordKey.ProducerKind, Is.EqualTo(ProducerKind.Mft));
        }
    }

    [Test]
    public async Task EnumerationSessionRetainsMarkersWithoutConnectingBroker()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository", isDirectory: false);
        files.AddMarker(".hidden/nested");
        await File.WriteAllTextAsync(Path.Combine(files.TreePath, "repository", "HEAD"), "ref: refs/heads/main");
        await File.WriteAllTextAsync(Path.Combine(files.TreePath, "repository", "ordinary.txt"), "not retained");
        var drive = new IndexedDrive('X', files.TreePath, 1, ProducerKind.Enumeration);
        await using var session = await MftIndexSession.OpenAsync([drive],
            BrokerTestHarness.CreateSession(_ => throw new AssertionException("Enumeration must not connect.")),
            files.BlockDirectory, null, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.Index.Search(new SearchQuery(".git", NameMatchMode.Exact)), Has.Count.EqualTo(2));
            Assert.That(session.Index.Search(new SearchQuery("HEAD", NameMatchMode.Exact)).Single().IsDirectory, Is.False);
            Assert.That(session.Index.Search(new SearchQuery("ordinary.txt", NameMatchMode.Exact)), Is.Empty);
            Assert.That(session.Index.Search(new SearchQuery("nested", NameMatchMode.Exact)).Single().IsDirectory, Is.True);
        }
    }

    [Test]
    public async Task PersistentOpenKeepsCanonicalBlock()
    {
        await using var broker = new BlockBrokerFixture();
        var session = await MftIndexSession.OpenAsync([broker.Drive('X')],
            broker.CreateSession(), broker.CacheDirectory, null, CancellationToken.None);
        var canonical = SyntheticBlock.CachedPath(broker.CacheDirectory, 'X', 1);
        Assert.That(File.Exists(canonical), Is.True);
        await session.DisposeAsync();
        Assert.That(File.Exists(canonical), Is.True);
        await using var reopened = await MftIndexSession.OpenAsync([broker.Drive('X')],
            BrokerTestHarness.CreateSession(_ => throw new AssertionException("A cached block must not connect.")),
            broker.CacheDirectory, CacheMode.ExistingOnly, null, CancellationToken.None);
        Assert.That(reopened.CaptureDriveStatuses().Single().IndexStatus!.Block?.Origin,
            Is.EqualTo(BlockOrigin.WarmStartedFromCache));
    }

    [Test]
    public async Task ScanProgressFrameReachesOpenProgress()
    {
        await using var broker = new BlockBrokerFixture();
        broker.ParseProgressToReport = (600_000, 1_200_000);
        var samples = new ConcurrentQueue<IndexScanProgress>();
        // The host keeps only the newest unsent progress sample, so hold the scan until the client has
        // relayed the parse sample; the block writer's own progress would otherwise replace it.
        broker.AfterParseProgressAsync = _ => AsyncCondition.WaitUntilAsync(
            () => samples.Any(sample => sample.Phase == IndexScanPhase.ParsingMft));
        await using var session = await MftIndexSession.OpenAsync([broker.Drive('X')],
            broker.CreateSession(), broker.CacheDirectory, new ProgressSink<IndexScanProgress>(samples.Enqueue),
            CancellationToken.None);

        Assert.That(samples.Where(sample => sample.Phase == IndexScanPhase.ParsingMft).ToArray(), Has.Length.EqualTo(1));
        var sample = samples.Single(candidate => candidate.Phase == IndexScanPhase.ParsingMft);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sample.Drive, Is.EqualTo((DriveKey)'X'));
            Assert.That(sample.Phase, Is.EqualTo(IndexScanPhase.ParsingMft));
            Assert.That(sample.WrittenRowCount, Is.EqualTo(600_000u));
            Assert.That(sample.ExpectedRowCount, Is.EqualTo(1_200_000u));
        }
    }

    sealed class ProgressSink<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    sealed class CountingLifetime(Action onDispose) : IAsyncDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count { get; private set; }

        public async ValueTask DisposeAsync()
        {
            Count++;
            onDispose();
            Started.SetResult();
            await Release.Task;
        }
    }
}
