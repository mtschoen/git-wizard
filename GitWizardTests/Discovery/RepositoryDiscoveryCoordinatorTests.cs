using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests.Discovery;

[TestFixture]
public class RepositoryDiscoveryCoordinatorTests
{

    static Func<IReadOnlyList<string>, CacheMode, IProgress<IndexScanProgress>?, CancellationToken,
        Task<MftIndexSession>> OpenWithBroker(BlockBrokerFixture broker, IndexedDrive drive, string cache) =>
        (_, cacheMode, _, token) => MftIndexSession.OpenAsync([drive], broker.CreateSession(), cache, cacheMode, null, token);

    static Func<IReadOnlyList<string>, CacheMode, IProgress<IndexScanProgress>?, CancellationToken,
        Task<MftIndexSession>> OpenWithIdentityFailure(BlockBrokerFixture broker) =>
        (drives, _, _, token) => MftIndexSession.OpenSelectedVolumesAsync(drives,
            _ => throw new IOException("Volume identity could not be acquired"), broker.CreateSession(),
            broker.CacheDirectory, null, token);

    [Test]
    public async Task OrdinaryAllowedNonemptyList_UsesList_NoIndexOpen_NoCrawler()
    {
        using var files = new PackedIndexFixture();
        var openCount = 0;
        var crawlCount = 0;
        var configuration = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, true, false, []);

        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "C", null),
            CrawlRootAsync = (_, _, _) => { Interlocked.Increment(ref crawlCount); return Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())); },
            OpenIndexAsync = (_, _, _, _) => { Interlocked.Increment(ref openCount); throw new InvalidOperationException("Index should not be opened"); },
            ReadListAsync = _ => Task.FromResult<string[]?>(new[] { @"C:\repos\r1" }),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.CompleteNonempty));
        Assert.That(result.Inventory.Observations.Single().Source, Is.EqualTo(DiscoverySource.RepositoryList));
        Assert.That(result.Inventory.Paths, Is.EqualTo(new[] { @"C:\repos\r1" }));
        Assert.That(openCount, Is.Zero);
        Assert.That(crawlCount, Is.Zero);
    }

    [Test]
    public async Task ListMiss_ReadySnapshot_UsesSnapshotWithoutBrokerConnection()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo1");

        var brokerConnections = 0;
        var crawlerCalls = new List<string>();
        var updates = new RecordingUpdateHandler();
        using var cancellation = new CancellationTokenSource();

        var configuration = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var openSession = StatusSessions.Opener(files, ('X', SyntheticDriveBehavior.Ready));
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, root, _) => { crawlerCalls.Add(root); return Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())); },
            OpenIndexAsync = (drives, cacheMode, progress, token) =>
            {
                Assert.That(cacheMode, Is.EqualTo(CacheMode.ExistingOnly));
                Assert.That(drives, Is.EquivalentTo(new[] { "X" }));
                return openSession(drives, cacheMode, progress, token);
            },
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, updates, cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(result.Inventory.Observations.Single().Source, Is.EqualTo(DiscoverySource.Snapshot));
            Assert.That(result.Inventory.Observations.Single().ScanTimestamp, Is.EqualTo(PackedIndexFixture.CompletionTimestamp));
            Assert.That(brokerConnections, Is.Zero);
            Assert.That(crawlerCalls, Is.Empty);
            Assert.That(updates.Messages.Any(message => message is not null && message.Contains("snapshot", StringComparison.OrdinalIgnoreCase)), Is.True);
        });
    }

    [TestCase(false)]
    [TestCase(true, TestName = "Issue370_ListMiss_SameTagDifferentShape_Rescans")]
    public async Task ListMiss_CacheDeclined_RescansAndUsesScanSource(bool seedDifferentRetention)
    {
        await using var broker = new BlockBrokerFixture();
        var cache = MftIndexCachePolicy.GetCacheDirectory(broker.CacheDirectory);
        if (seedDifferentRetention)
        {
            await using var seedBroker = broker.CreateSession();
            await using var seed = await FileIndex.OpenAsync(new FileIndexOptions
            {
                Drives = [broker.Drive('X')],
                CacheDirectory = cache,
                CacheTag = MftIndexCachePolicy.CacheTag,
                MftSource = seedBroker.CreateIndexSource()
            }, CancellationToken.None);
            Assert.That(seed.Drives.Single().Block?.Origin, Is.EqualTo(BlockOrigin.ProducedByScan));
        }

        await using var session = await MftIndexSession.OpenAsync([broker.Drive('X')], broker.CreateSession(),
            cache, CacheMode.ExistingOnly, null, CancellationToken.None);
        Assert.That(session.CaptureDriveStatuses().Single().IndexStatus!.State, Is.EqualTo(DriveState.Failed));
        Assert.That(session.CaptureDriveStatuses().Single().IndexStatus!.Failure?.Kind,
            Is.EqualTo(seedDifferentRetention
                ? DriveFailureKind.CacheIdentityMismatch
                : DriveFailureKind.CacheDeclined));

        var configuration = new GitWizardConfiguration { SearchPaths = { broker.Drive('X').RootDirectory } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var openedSession = session;
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(openedSession),
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        Assert.That(result.Inventory.Observations.Single().Source, Is.EqualTo(DiscoverySource.Scan));
        Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.CompleteNonempty));
        Assert.That(broker.ConnectionCount, Is.EqualTo(seedDifferentRetention ? 2 : 1));
        Assert.That(broker.Scans.Count, Is.EqualTo(seedDifferentRetention ? 2 : 1));
    }

    [Test]
    public async Task ExplicitRebuild_WarmSnapshot_RescansAndUsesScanSource()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo1");
        await using var index = await files.OpenIndexAsync();

        var configuration = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, true, false, []);

        var fileIndex = index;
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(new MftIndexSession(fileIndex, null, ["X"], [], new Dictionary<string, string>())),
            ReadListAsync = _ => Task.FromResult<string[]?>(new[] { @"C:\old" }),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        Assert.That(result.Inventory.Observations.Single().Source, Is.EqualTo(DiscoverySource.Scan));
    }

    [Test]
    public async Task SnapshotInUse_ReportsRefusalWithoutReopeningOrCrawling()
    {
        await using var holderBroker = new BlockBrokerFixture();
        await using var refusedBroker = new BlockBrokerFixture();
        var drive = holderBroker.Drive('X');
        var cache = MftIndexCachePolicy.GetCacheDirectory(holderBroker.CacheDirectory);
        await using var holder = await MftIndexSession.OpenAsync([drive], holderBroker.CreateSession(),
            cache, CacheMode.Persistent, null, CancellationToken.None);

        var crawlerCalls = 0;
        var configuration = new GitWizardConfiguration { SearchPaths = { drive.RootDirectory } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => { Interlocked.Increment(ref crawlerCalls); return Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())); },
            OpenIndexAsync = OpenWithBroker(refusedBroker, drive, cache),
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Unusable));
        Assert.That(crawlerCalls, Is.Zero);
        Assert.That(refusedBroker.ConnectionCount, Is.Zero);
    }

    [Test]
    public async Task IdentityFailure_RecordsErrorAndRetainsRequestedDrive()
    {
        await using var broker = new BlockBrokerFixture();

        var configuration = new GitWizardConfiguration { SearchPaths = { broker.Drive('X').RootDirectory } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = OpenWithIdentityFailure(broker),
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Unusable));
        Assert.That(result.Inventory.RequestedNtfsDrives, Is.EquivalentTo(new[] { "X" }));
        Assert.That(result.Inventory.Errors, Does.ContainKey("X").WithValue("Volume identity could not be acquired"));
    }

    [Test]
    public async Task OneHealthyEmpty_OneFailed_IsPartialZero_RetainsFailedListEntries()
    {
        using var files = new PackedIndexFixture();

        var writtenPaths = new List<string[]>();
        var configuration = new GitWizardConfiguration { SearchPaths = { @"C:\repos", @"D:\repos" } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, false, []);

        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => root.StartsWith(@"C:\", StringComparison.Ordinal)
                ? new RootClassification(root, RootFileSystem.Ntfs, "C", null)
                : new RootClassification(root, RootFileSystem.Ntfs, "D", "Drive D is offline"),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = StatusSessions.Opener(files,
                ('C', SyntheticDriveBehavior.Ready), ('D', SyntheticDriveBehavior.Offline)),
            ReadListAsync = _ => Task.FromResult<string[]?>(new[] { @"C:\repos\gone", @"D:\repos\keep" }),
            WriteListAsync = (paths, _) => { writtenPaths.Add(paths.ToArray()); return Task.CompletedTask; },
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        var scope = DiscoveryScope.CreateExpanded(request, configuration.SearchPaths.ToArray(),
            request.RetainedRoots, [], dependencies.ClassifyRoot);
        await using var result = await coordinator.DiscoverScopeAsync(request, scope, null, null, CancellationToken.None);

        Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Partial));
        Assert.That(result.Inventory.Paths, Is.Empty);
        Assert.That(writtenPaths.Single(), Is.EquivalentTo(new[] { @"D:\repos\keep" }));
    }

    [Test]
    public async Task EveryDriveFailed_IsUnusable_ListWriterNeverCalled()
    {
        using var files = new PackedIndexFixture();

        var listWriterCalled = false;
        var configuration = new GitWizardConfiguration { SearchPaths = { @"C:\repos" } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "C", "offline"),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = StatusSessions.Opener(files, ('C', SyntheticDriveBehavior.Offline)),
            ReadListAsync = _ => Task.FromResult<string[]?>(new[] { @"C:\repos\keep" }),
            WriteListAsync = (_, _) => { listWriterCalled = true; return Task.CompletedTask; },
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        var scope = DiscoveryScope.CreateExpanded(request, configuration.SearchPaths.ToArray(),
            request.RetainedRoots, [], dependencies.ClassifyRoot);
        await using var result = await coordinator.DiscoverScopeAsync(request, scope, null, null, CancellationToken.None);

        Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Unusable));
        Assert.That(listWriterCalled, Is.False);
    }

    [Test]
    public async Task FreshGloballyEmpty_IsCompleteEmpty_ListWriterCalledWithEmptyArray()
    {
        using var files = new PackedIndexFixture();

        var writtenPaths = new List<string[]>();
        var configuration = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, false, []);

        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = StatusSessions.Opener(files, ('X', SyntheticDriveBehavior.Ready)),
            ReadListAsync = _ => Task.FromResult<string[]?>(new[] { @"C:\repos\old" }),
            WriteListAsync = (paths, _) => { writtenPaths.Add(paths.ToArray()); return Task.CompletedTask; },
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.CompleteEmpty));
        Assert.That(writtenPaths.Single(), Is.Empty);
    }

    [Test]
    public async Task RescanPreservesOldReadyBlockPlusProducerError_IsUnusable_NeverCurrentSuccess()
    {
        using var files = new PackedIndexFixture();

        var configuration = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, false, []);

        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = StatusSessions.OpenerFailingRescan(files, "MFT producer failed during rescan",
                ('X', SyntheticDriveBehavior.Ready)),
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Unusable));
        Assert.That(result.Inventory.CanPublishFullInventory, Is.False);
        Assert.That(result.Inventory.Errors, Does.ContainKey("X").WithValue("MFT producer failed during rescan"));
    }
}
