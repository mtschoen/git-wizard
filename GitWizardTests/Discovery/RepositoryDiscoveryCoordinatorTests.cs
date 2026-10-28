using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;

namespace GitWizardTests.Discovery;

[TestFixture]
public class RepositoryDiscoveryCoordinatorTests
{
    [TestCase(true, 0)]
    [TestCase(false, 1)]
    public void IncompleteWalkStatusCannotPublishEmptyInventory(bool exhausted, int deniedSubtrees)
    {
        using var files = new PackedIndexFixture();
        var request = new DiscoveryRequest(new GitWizardConfiguration(), DiscoveryPurpose.Ordinary, false, false, []);
        var scope = DiscoveryScope.CreateExpanded(request, [files.TreePath], [], []);
        var block = MFTLibTestExtensions.SyntheticNotifications.CreateRootBlockStatus(
            BlockOrigin.ProducedByScan, 1, exhausted, PackedIndexFixture.CompletionTimestamp) with
        { AccessDeniedSubtreeCount = deniedSubtrees };
        var status = MFTLibTestExtensions.SyntheticNotifications.CreateRootStatus(RootKey.FromDirectory(files.TreePath),
            RootState.Ready, block, null) with
        { Producer = ProducerKind.Enumeration };
        var accumulator = new DiscoveryAccumulator(scope, new List<string>());
        accumulator.RecordDriveStatuses([new MftRootStatus(files.TreePath, status, null)]);
        accumulator.AddIndexedObservations(scope.SearchRoots, false, DiscoverySource.Scan, null);

        var inventory = accumulator.BuildInventory(DiscoveryRoute.RecursiveOnly, []);

        Assert.That(inventory.CanPublishFullInventory, Is.False);
    }


    [TestCase(@"c:\", @"c:\repos\one")]
    [TestCase(@"D:\", @"D:\repos\one")]
    public void VolumeRootObservationMatchesStatusKeyedByRootKey(string indexRoot, string repository)
    {
        var request = new DiscoveryRequest(new GitWizardConfiguration(), DiscoveryPurpose.Ordinary, false, false, []);
        var scope = DiscoveryScope.CreateExpanded(request, [indexRoot], [], []);
        var driveKey = RootKeyProjection.ParseRootKeys([indexRoot]).Single().ToString();
        var accumulator = new DiscoveryAccumulator(scope, new List<string> { repository });

        accumulator.RecordDriveStatuses([ReadyStatus(driveKey, RootKeyProjection.ParseRootKeys([indexRoot]).Single())]);
        accumulator.AddIndexedObservations(scope.SearchRoots, false, DiscoverySource.Snapshot, null);
        var inventory = accumulator.BuildInventory(DiscoveryRoute.Indexed, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inventory.Outcome, Is.EqualTo(DiscoveryOutcome.CompleteNonempty));
            Assert.That(inventory.Observations.Single().Complete, Is.True);
            Assert.That(inventory.Observations.Single().Root, Is.EqualTo(indexRoot));
        }
    }

    [Test]
    public void VolumeRootRescanAndFailureAreFoundByRootKey()
    {
        var request = new DiscoveryRequest(new GitWizardConfiguration(), DiscoveryPurpose.Ordinary, false, false, []);
        var scope = DiscoveryScope.CreateExpanded(request, [@"c:\", @"d:\"], [], []);
        var accumulator = new DiscoveryAccumulator(scope, new List<string>());
        var failed = ReadyStatus("D", RootKeyProjection.ParseRootKeys([@"d:\"]).Single()) with { IndexStatus = null };
        accumulator.RecordDriveStatuses([ReadyStatus("C", RootKeyProjection.ParseRootKeys([@"c:\"]).Single()), failed]);
        accumulator.RescannedDrives.Add("C");
        accumulator.AddIndexedObservations(scope.SearchRoots, false, DiscoverySource.Snapshot, null);

        var observations = accumulator.Observations;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(observations[0].Source, Is.EqualTo(DiscoverySource.Scan));
            Assert.That(observations[0].Complete, Is.True);
            Assert.That(observations[1].Complete, Is.False);
            Assert.That(observations[1].Error, Does.Contain("no index status"));
        }
    }

    static MftRootStatus ReadyStatus(string drive, RootKey root)
    {
        var block = MFTLibTestExtensions.SyntheticNotifications.CreateRootBlockStatus(
            BlockOrigin.ProducedByScan, 1, false, PackedIndexFixture.CompletionTimestamp);
        var status = MFTLibTestExtensions.SyntheticNotifications.CreateRootStatus(root, RootState.Ready, block, null) with
        { Producer = ProducerKind.Mft };
        return new MftRootStatus(drive, status, null);
    }


    static Func<IReadOnlyList<string>, CacheMode, IProgress<IndexScanProgress>?, CancellationToken,
        Task<MftIndexSession>> OpenWithBroker(BlockBrokerFixture broker, RootKey drive, string cache) =>
        (_, cacheMode, _, token) => MftIndexSession.OpenAsync([drive], broker.CreateSession(), new MftIndexSession.CacheSelection(cache, cacheMode), null, token, configureOptions: broker.ConfigureOptions);

    static Func<IReadOnlyList<string>, CacheMode, IProgress<IndexScanProgress>?, CancellationToken,
        Task<MftIndexSession>> OpenWithIdentityFailure(BlockBrokerFixture broker) =>
        (drives, _, _, token) => MftIndexSession.OpenSelectedVolumesAsync(drives,
            broker.CreateSession(), broker.CacheDirectory, null, token,
            options => broker.ConfigureOptions(options) with { Producer = ProducerSelection.MftOnly, MftSource = null });

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
            CrawlRootAsync = (_, _, _) => { Interlocked.Increment(ref crawlCount); return Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())); },
            OpenIndexAsync = (_, _, _, _) => { Interlocked.Increment(ref openCount); throw new InvalidOperationException("Index should not be opened"); },
            ReadListAsync = _ => Task.FromResult<string[]?>([@"C:\repos\r1"]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.CompleteNonempty));
            Assert.That(result.Inventory.Observations.Single().Source, Is.EqualTo(DiscoverySource.RepositoryList));
            Assert.That(result.Inventory.Paths, Is.EqualTo([@"C:\repos\r1"]));
            Assert.That(openCount, Is.Zero);
            Assert.That(crawlCount, Is.Zero);
        }
    }

    [Test]
    public async Task ListMiss_ReadySnapshot_UsesSnapshotWithoutBrokerConnection()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo1");

        const int brokerConnections = 0;
        var crawlerCalls = new List<string>();
        var updates = new RecordingUpdateHandler();
        using var cancellation = new CancellationTokenSource();

        var configuration = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var openSession = StatusSessions.Opener(files, ('X', SyntheticDriveBehavior.Ready));
        var treePath = files.TreePath;
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            CrawlRootAsync = (_, root, _) => { crawlerCalls.Add(root); return Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())); },
            OpenIndexAsync = (drives, cacheMode, progress, token) =>
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(cacheMode, Is.EqualTo(CacheMode.ExistingOnly));
                    Assert.That(drives, Is.EquivalentTo([treePath]).IgnoreCase);
                }
                return openSession(drives, cacheMode, progress, token);
            },
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, updates, cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inventory.Observations.Single().Source, Is.EqualTo(DiscoverySource.Snapshot));
            Assert.That(result.Inventory.Observations.Single().ScanTimestamp, Is.EqualTo(PackedIndexFixture.CompletionTimestamp));
            Assert.That(brokerConnections, Is.Zero);
            Assert.That(crawlerCalls, Is.Empty);
            Assert.That(updates.Messages.Any(message => message?.Contains("snapshot", StringComparison.OrdinalIgnoreCase) == true), Is.True);
        }
    }

    [TestCase(false)]
    [TestCase(true, TestName = "Issue370_ListMiss_SameTagDifferentShape_Rescans")]
    public async Task ListMiss_CacheDeclined_RescansAndUsesScanSource(bool seedDifferentShape)
    {
        await using var broker = new BlockBrokerFixture();
        var cache = MftIndexCachePolicy.GetCacheDirectory(broker.CacheDirectory);
        if (seedDifferentShape)
        {
            await using var seedBroker = broker.CreateSession();
            await using var seed = await FileIndex.OpenAsync(MFTLibTestExtensions.SyntheticVolumeFacts.Apply(new FileIndexOptions
            {
                Root = broker.Drive('X'),
                CacheDirectory = cache,
                CacheTag = MftIndexCachePolicy.CacheTag,
                MftSource = seedBroker.CreateIndexSource(new MftScanOptions { IncludeFreed = true })
            }, 1, volume: new VolumeKey('X'), volumeRootDirectory: broker.TreePath), CancellationToken.None);
            Assert.That(seed.Status.Block?.Origin, Is.EqualTo(BlockOrigin.ProducedByScan));
        }

        await using var session = await MftIndexSession.OpenAsync([RootKey.FromDirectory(broker.TreePath)], broker.CreateSession(),
            new MftIndexSession.CacheSelection(cache, CacheMode.ExistingOnly), null, CancellationToken.None, configureOptions: broker.ConfigureOptions);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.CaptureRootStatuses().Single().IndexStatus!.State, Is.EqualTo(RootState.Failed));
            Assert.That(session.CaptureRootStatuses().Single().IndexStatus!.Failure?.Kind,
                Is.EqualTo(seedDifferentShape
                    ? RootFailureKind.CacheIdentityMismatch
                    : RootFailureKind.CacheDeclined));
        }

        var configuration = new GitWizardConfiguration { SearchPaths = { broker.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var sessionTask = Task.FromResult(session);
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = async (_, _, _, _) => await sessionTask,
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inventory.Observations.Single().Source, Is.EqualTo(DiscoverySource.Scan));
            Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.CompleteNonempty));
            Assert.That(broker.ConnectionCount, Is.EqualTo(seedDifferentShape ? 2 : 1));
            Assert.That(broker.Scans, Has.Count.EqualTo(seedDifferentShape ? 2 : 1));
        }
    }

    [Test]
    public async Task ExplicitRebuild_WarmSnapshot_RescansAndUsesScanSource()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo1");
        await using var index = await files.OpenIndexAsync(root: RootKey.FromDirectory(files.TreePath));

        var configuration = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, true, false, []);

        var indexTask = Task.FromResult(index);
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = async (roots, _, _, _) => new MftIndexSession([await indexTask], null, roots, new Dictionary<string, string>()),
            ReadListAsync = _ => Task.FromResult<string[]?>([@"C:\old"]),
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
            new MftIndexSession.CacheSelection(cache, CacheMode.Persistent), null, CancellationToken.None, configureOptions: holderBroker.ConfigureOptions);

        var crawlerCalls = 0;
        var configuration = new GitWizardConfiguration { SearchPaths = { holderBroker.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            CrawlRootAsync = (_, _, _) => { Interlocked.Increment(ref crawlerCalls); return Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())); },
            OpenIndexAsync = OpenWithBroker(refusedBroker, drive, cache),
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Unusable));
            Assert.That(crawlerCalls, Is.Zero);
            Assert.That(refusedBroker.ConnectionCount, Is.Zero);
        }
    }

    [Test]
    public async Task IdentityFailure_RecordsErrorAndRetainsRequestedDrive()
    {
        await using var broker = new BlockBrokerFixture();

        var configuration = new GitWizardConfiguration { SearchPaths = { broker.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = OpenWithIdentityFailure(broker),
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Unusable));
            Assert.That(result.Inventory.RootStatuses.Single().IndexStatus!.MftUnavailable, Is.EqualTo(MftUnavailableReason.NoMftSource));
            Assert.That(result.Inventory.Errors.Keys, Does.Contain(GitWizardApi.NormalizePath(broker.TreePath)).IgnoreCase);
        }
    }

    [Test]
    public async Task OneHealthyEmpty_OneFailed_IsPartialZero_RetainsFailedListEntries()
    {
        using var files = new PackedIndexFixture();

        var writtenPaths = new List<string[]>();
        var failedRoot = Path.Combine(files.DirectoryPath, "unavailable");
        var treePath = files.TreePath;
        var configuration = new GitWizardConfiguration { SearchPaths = { files.TreePath, failedRoot } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, false, []);

        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = StatusSessions.Opener(files,
                ('C', SyntheticDriveBehavior.Ready), ('D', SyntheticDriveBehavior.Offline)),
            ReadListAsync = _ => Task.FromResult<string[]?>([Path.Combine(treePath, "gone"), Path.Combine(failedRoot, "keep")]),
            WriteListAsync = (paths, _) => { writtenPaths.Add([.. paths]); return Task.CompletedTask; },
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        var scope = DiscoveryScope.CreateExpanded(request, (string[])[.. configuration.SearchPaths],
            request.RetainedRoots, []);
        await using var result = await coordinator.DiscoverScopeAsync(request, scope, null, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Partial));
            Assert.That(result.Inventory.Paths, Is.Empty);
            Assert.That(writtenPaths.Single(), Is.EquivalentTo([Path.Combine(failedRoot, "keep")]).IgnoreCase);
        }
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
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = StatusSessions.Opener(files, ('C', SyntheticDriveBehavior.Offline)),
            ReadListAsync = _ => Task.FromResult<string[]?>([@"C:\repos\keep"]),
            WriteListAsync = (_, _) => { listWriterCalled = true; return Task.CompletedTask; },
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        var scope = DiscoveryScope.CreateExpanded(request, (string[])[.. configuration.SearchPaths],
            request.RetainedRoots, []);
        await using var result = await coordinator.DiscoverScopeAsync(request, scope, null, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Unusable));
            Assert.That(listWriterCalled, Is.False);
        }
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
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = StatusSessions.Opener(files, ('X', SyntheticDriveBehavior.Ready)),
            ReadListAsync = _ => Task.FromResult<string[]?>([@"C:\repos\old"]),
            WriteListAsync = (paths, _) => { writtenPaths.Add([.. paths]); return Task.CompletedTask; },
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.CompleteEmpty));
            Assert.That(writtenPaths.Single(), Is.Empty);
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Unusable));
            Assert.That(result.Inventory.CanPublishFullInventory, Is.False);
            Assert.That(result.Inventory.Errors, Does.ContainKey(GitWizardApi.NormalizePath(files.TreePath)).WithValue("MFT producer failed during rescan"));
        }
    }
}
