using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Discovery;

[TestFixture]
[SupportedOSPlatform("linux")]
public class LinuxWatchStartupTests
{

    string _temporaryRoot = null!;

    [SetUp]
    public void CreateTemporaryRoot() =>
        _temporaryRoot = Directory.CreateTempSubdirectory("git-wizard-linux-startup").FullName;

    [TearDown]
    public void DeleteTemporaryRoot() => Directory.Delete(_temporaryRoot, true);

    string CreateSearchRoot(string name) => Directory.CreateDirectory(Path.Combine(_temporaryRoot, name)).FullName;

    sealed class ScriptedLinuxVolumeSource : IVolumeChangeSource, IVolumeChangeDiagnostics
    {
        public event Action<string>? SourceDied { add { } remove { } }
        public event Action<string, string>? DriveFailed { add { } remove { } }
        public event Action<JournalWarning>? JournalWarningDetected { add { } remove { } }

        public TaskCompletionSource MarksInstalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, string> ArmErrors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Channel<VolumeChangeBatch> EventChannel { get; } = Channel.CreateUnbounded<VolumeChangeBatch>();
        public bool Disposed { get; private set; }

        public Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct)
        {
            MarksInstalled.TrySetResult();
            return Task.FromResult(new VolumeArmResult(ArmErrors, []));
        }

        public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var batch in EventChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return batch;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            EventChannel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task MarksInstalledBeforeCrawl_AndQueuedEventsDispatchedAfterInventoryReady()
    {
        var source = new ScriptedLinuxVolumeSource();
        var marksInstalled = source.MarksInstalled;
        var crawlEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCrawl = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var searchRoot = CreateSearchRoot("repos");
        var repositoryPath = Path.Combine(searchRoot, "myrepo");

        async Task<RecursiveDiscovery> Crawl(DiscoveryRequest request, string root, CancellationToken token)
        {
            Assert.That(marksInstalled.Task.IsCompletedSuccessfully, Is.True, "Marks must be installed before recursive crawl begins.");
            crawlEntered.TrySetResult();
            await finishCrawl.Task.WaitAsync(token);
            return new RecursiveDiscovery([repositoryPath], [], new Dictionary<string, string>());
        }

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = false,
            IsLinux = true,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Recursive, null, null),
            CrawlRootAsync = Crawl,
            OpenIndexAsync = (_, _, _, _) => throw new PlatformNotSupportedException(),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => source
        });

        var config = new GitWizardConfiguration { SearchPaths = { searchRoot } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cts = new CancellationTokenSource(TestTimeouts.HangGuard);

        var startupTask = coordinator.PrepareWatchAsync(request, null, cts.Token);
        await crawlEntered.Task.WaitAsync(cts.Token);

        // While crawl is in flight, enqueue an edit event for the repository
        source.EventChannel.Writer.TryWrite(new VolumeChangeBatch("/", [
            new VolumeChangeEntry(repositoryPath + "/file.txt", VolumeEntryKind.Modified)
        ]));

        finishCrawl.TrySetResult();
        await using var startup = await startupTask.WaitAsync(TestTimeouts.HangGuard);

        Assert.That(startup.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.CompleteNonempty));
        Assert.That(startup.Inventory.Paths, Is.EquivalentTo([repositoryPath]));

        var inventoryFired = false;
        var startedFired = false;
        var inventoryBeforeStarted = false;

        var service = new RepositoryWatchService(startup);
        service.InventoryReady += _ =>
        {
            inventoryFired = true;
            if (!startedFired)
            {
                inventoryBeforeStarted = true;
            }
        };
        service.Started += () => startedFired = true;

        await using var enumerator = service.RunAsync(cts.Token).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();

        Assert.That(await moveNext.WaitAsync(TestTimeouts.HangGuard), Is.True);
        Assert.That(inventoryFired, Is.True);
        Assert.That(startedFired, Is.True);
        Assert.That(inventoryBeforeStarted, Is.True, "InventoryReady must fire before Started.");
        Assert.That(enumerator.Current.RepoRoot, Is.EqualTo(repositoryPath));
        Assert.That(enumerator.Current.Kind, Is.EqualTo(RepositoryChangeKind.Changed));
    }

    [Test]
    public async Task FanotifyNative_QueueOverflow_ReportsSourceDied()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(5, 0)
        };
        // Construct an overflow event buffer
        var overflowBuffer = new byte[32];
        BitConverter.TryWriteBytes(overflowBuffer.AsSpan(0, 4), (uint)32); // event_len
        overflowBuffer[4] = 3; // version 3
        BitConverter.TryWriteBytes(overflowBuffer.AsSpan(8, 8), FanotifyConstants.QueueOverflow); // mask
        native.ReadQueue.Enqueue(overflowBuffer);

        var source = new FanotifyVolumeChangeSource(native);
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = false,
            IsLinux = true,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Recursive, null, null),
            CrawlRootAsync = (_, root, _) => Task.FromResult(new RecursiveDiscovery([Path.Combine(root, "repo")], [], new Dictionary<string, string>())),
            OpenIndexAsync = (_, _, _, _) => throw new PlatformNotSupportedException(),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => source
        });

        var config = new GitWizardConfiguration { SearchPaths = { TempDirectory.SystemTempPath } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cts = new CancellationTokenSource(TestTimeouts.HangGuard);

        await using var startup = await coordinator.PrepareWatchAsync(request, null, cts.Token);
        var service = new RepositoryWatchService(startup);
        var stoppedReason = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Stopped += reason => stoppedReason.TrySetResult(reason);

        await using var reader = service.RunAsync(cts.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();

        var diedReason = await stoppedReason.Task.WaitAsync(TestTimeouts.HangGuard);
        Assert.That(diedReason, Does.Contain("overflow"));
        Assert.That(await pending.WaitAsync(TestTimeouts.HangGuard), Is.False);
    }

    [Test]
    public async Task OneFailedMark_PreservesFailedScopeAndContinuesHealthy()
    {
        var healthyRoot = CreateSearchRoot("healthy");
        var failedRoot = CreateSearchRoot("failed");
        var source = new ScriptedLinuxVolumeSource();
        source.ArmErrors[failedRoot] = "fanotify_mark failed: EPERM";

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = false,
            IsLinux = true,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Recursive, null, null),
            CrawlRootAsync = (_, root, _) => Task.FromResult(new RecursiveDiscovery([Path.Combine(root, "healthy-repo")], [], new Dictionary<string, string>())),
            OpenIndexAsync = (_, _, _, _) => throw new PlatformNotSupportedException(),
            ReadListAsync = _ => Task.FromResult<string[]?>([Path.Combine(failedRoot, "old-repo")]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => source
        });

        var config = new GitWizardConfiguration { SearchPaths = { healthyRoot, failedRoot } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cts = new CancellationTokenSource(TestTimeouts.HangGuard);

        await using var startup = await coordinator.PrepareWatchAsync(request, null, cts.Token);

        Assert.That(startup.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Partial));
        Assert.That(startup.Inventory.Route, Is.EqualTo(DiscoveryRoute.RecursiveOnly));
        Assert.That(startup.Source, Is.Not.Null, "Healthy mark must keep source active.");
        Assert.That(startup.Inventory.Errors, Does.ContainKey(failedRoot));
        var failedObservation = startup.Inventory.Observations.Single(o => o.Root == failedRoot);
        Assert.That(failedObservation.Complete, Is.False);
        Assert.That(failedObservation.Error, Is.EqualTo("fanotify_mark failed: EPERM"));
    }

    [Test]
    public async Task AllMarksFail_DisposesSourceAndReturnsUnusable()
    {
        var firstRoot = CreateSearchRoot("one");
        var secondRoot = CreateSearchRoot("two");
        var source = new ScriptedLinuxVolumeSource();
        source.ArmErrors[firstRoot] = "fanotify_mark failed: EPERM";
        source.ArmErrors[secondRoot] = "fanotify_mark failed: EPERM";

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = false,
            IsLinux = true,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Recursive, null, null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("Crawl must not happen when all marks fail."),
            OpenIndexAsync = (_, _, _, _) => throw new PlatformNotSupportedException(),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => source
        });

        var config = new GitWizardConfiguration { SearchPaths = { firstRoot, secondRoot } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cts = new CancellationTokenSource(TestTimeouts.HangGuard);

        await using var startup = await coordinator.PrepareWatchAsync(request, null, cts.Token);

        Assert.That(startup.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Unusable));
        Assert.That(startup.Source, Is.Null, "Wholly failed marks must dispose source.");
        Assert.That(source.Disposed, Is.True);
    }

    [Test]
    public async Task CancellationWhileCrawling_DisposesSource()
    {
        var searchRoot = CreateSearchRoot("repos");
        var source = new ScriptedLinuxVolumeSource();
        var crawlEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<RecursiveDiscovery> Crawl(DiscoveryRequest request, string root, CancellationToken token)
        {
            crawlEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new RecursiveDiscovery([], [], new Dictionary<string, string>());
        }

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = false,
            IsLinux = true,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Recursive, null, null),
            CrawlRootAsync = Crawl,
            OpenIndexAsync = (_, _, _, _) => throw new PlatformNotSupportedException(),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => source
        });

        var config = new GitWizardConfiguration { SearchPaths = { searchRoot } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cts = new CancellationTokenSource();

        var startupTask = coordinator.PrepareWatchAsync(request, null, cts.Token);
        await crawlEntered.Task.WaitAsync(TestTimeouts.HangGuard);
        await cts.CancelAsync();

        Assert.CatchAsync<OperationCanceledException>(async () => await startupTask.WaitAsync(TestTimeouts.HangGuard));
        Assert.That(source.Disposed, Is.True, "Cancelled preparation must dispose source.");
    }
}
