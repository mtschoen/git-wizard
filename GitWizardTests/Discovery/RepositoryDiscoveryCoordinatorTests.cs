using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Discovery;

[TestFixture]
public class RepositoryDiscoveryCoordinatorTests
{
    sealed class TestUpdateHandler : IUpdateHandler
    {
        readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages => _messages;
        public void SendUpdateMessage(string? message)
        {
            if (message is not null)
                _messages.Add(message);
        }
        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) { }
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    }

    static DriveStatus CreateDriveStatus(
        char letter,
        DriveState state,
        DriveFailureKind? failureKind = null,
        DateTime? scanTimestamp = null,
        string? failureMessage = null,
        BlockSource blockSource = BlockSource.ProducedByScan)
    {
        var block = state == DriveState.Ready
            ? SyntheticNotifications.CreateDriveBlockStatus(blockSource, 10, false, scanTimestamp ?? DateTime.UtcNow)
            : null;
        var failure = failureKind is { } kind
            ? SyntheticNotifications.CreateDriveFailure(kind, failureMessage ?? kind.ToString())
            : null;
        var status = SyntheticNotifications.CreateDriveStatus(letter, state, block, failure);
        return status with { Watch = status.Watch with { Supported = state == DriveState.Ready } };
    }

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
        await using var index = await files.OpenIndexAsync();

        var brokerConnections = 0;
        var crawlerCalls = new List<string>();
        var updates = new TestUpdateHandler();
        using var cancellation = new CancellationTokenSource();

        var configuration = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var fileIndex = index;
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, root, _) => { crawlerCalls.Add(root); return Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())); },
            OpenIndexAsync = (drives, intent, _, _) =>
            {
                Assert.That(intent, Is.EqualTo(MftIndexOpenIntent.Snapshot));
                Assert.That(drives, Is.EquivalentTo(new[] { "X" }));
                var status = CreateDriveStatus('X', DriveState.Ready,
                    scanTimestamp: PackedIndexFixture.CompletionTimestamp);
                var session = new MftIndexSession(fileIndex, null, ["X"], [], new Dictionary<string, string>())
                {
                    DriveStatusesOverride = () => [new MftDriveStatus("X", status, null)]
                };
                return Task.FromResult(session);
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
            Assert.That(updates.Messages.Any(message => message.Contains("snapshot", StringComparison.OrdinalIgnoreCase)), Is.True);
        });
    }

    [Test]
    public async Task ListMiss_CacheDeclined_RescansAndUsesScanSource()
    {
        await using var broker = new BlockBrokerFixture();
        var cache = MftIndexCachePolicy.GetCacheDirectory(broker.CacheDirectory);
        var session = await MftIndexSession.OpenAsync([broker.Drive('X')], broker.CreateSession(),
            cache, MftIndexOpenIntent.Snapshot, null, CancellationToken.None);

        var configuration = new GitWizardConfiguration { SearchPaths = { broker.Drive('X').RootDirectory } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(session),
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        Assert.That(result.Inventory.Observations.Single().Source, Is.EqualTo(DiscoverySource.Scan));
        Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.CompleteNonempty));
        Assert.That(broker.ConnectionCount, Is.EqualTo(1));
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
        using var files = new PackedIndexFixture();
        await using var index = await files.OpenIndexAsync();

        var crawlerCalls = 0;
        var configuration = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var fileIndex = index;
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => { Interlocked.Increment(ref crawlerCalls); return Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())); },
            OpenIndexAsync = (_, _, _, _) =>
            {
                var status = CreateDriveStatus('X', DriveState.Failed, DriveFailureKind.InUse,
                    failureMessage: "In-use index refusal");
                var session = new MftIndexSession(fileIndex, null, ["X"], [], new Dictionary<string, string> { ["X"] = "In-use index refusal" })
                {
                    DriveStatusesOverride = () => [new MftDriveStatus("X", status, null)]
                };
                return Task.FromResult(session);
            },
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        await using var result = await coordinator.DiscoverAsync(request, null, CancellationToken.None);

        Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Unusable));
        Assert.That(crawlerCalls, Is.Zero);
    }

    [Test]
    public async Task IdentityFailure_RecordsErrorAndRetainsRequestedDrive()
    {
        using var files = new PackedIndexFixture();
        await using var index = await files.OpenIndexAsync();

        var configuration = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var fileIndex = index;
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = (_, _, _, _) =>
            {
                var session = new MftIndexSession(fileIndex, null, ["X"], [], new Dictionary<string, string> { ["X"] = "Volume identity could not be acquired" })
                {
                    DriveStatusesOverride = () => [new MftDriveStatus("X", null, "Volume identity could not be acquired")]
                };
                return Task.FromResult(session);
            },
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
        await using var index = await files.OpenIndexAsync('C');

        var writtenPaths = new List<string[]>();
        var configuration = new GitWizardConfiguration { SearchPaths = { @"C:\repos", @"D:\repos" } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, false, []);

        var fileIndex = index;
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => root.StartsWith(@"C:\", StringComparison.Ordinal)
                ? new RootClassification(root, RootFileSystem.Ntfs, "C", null)
                : new RootClassification(root, RootFileSystem.Ntfs, "D", "Drive D is offline"),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = (_, _, _, _) =>
            {
                var cStatus = CreateDriveStatus('C', DriveState.Ready,
                    scanTimestamp: PackedIndexFixture.CompletionTimestamp);
                var dStatus = CreateDriveStatus('D', DriveState.Offline);
                var session = new MftIndexSession(fileIndex, null, ["C", "D"], [], new Dictionary<string, string> { ["D"] = "Drive D is offline" })
                {
                    DriveStatusesOverride = () => [new MftDriveStatus("C", cStatus, null), new MftDriveStatus("D", dStatus, "Drive D is offline")]
                };
                return Task.FromResult(session);
            },
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
        await using var index = await files.OpenIndexAsync();

        var listWriterCalled = false;
        var configuration = new GitWizardConfiguration { SearchPaths = { @"C:\repos" } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);

        var fileIndex = index;
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "C", "offline"),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = (_, _, _, _) =>
            {
                var cStatus = CreateDriveStatus('C', DriveState.Offline);
                var session = new MftIndexSession(fileIndex, null, ["C"], [], new Dictionary<string, string> { ["C"] = "offline" })
                {
                    DriveStatusesOverride = () => [new MftDriveStatus("C", cStatus, "offline")]
                };
                return Task.FromResult(session);
            },
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
        await using var index = await files.OpenIndexAsync();

        var writtenPaths = new List<string[]>();
        var configuration = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, false, []);

        var fileIndex = index;
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = (_, _, _, _) =>
            {
                var xStatus = CreateDriveStatus('X', DriveState.Ready,
                    scanTimestamp: DateTime.UtcNow);
                var session = new MftIndexSession(fileIndex, null, ["X"], [], new Dictionary<string, string>())
                {
                    DriveStatusesOverride = () => [new MftDriveStatus("X", xStatus, null)]
                };
                return Task.FromResult(session);
            },
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
        await using var index = await files.OpenIndexAsync();

        var configuration = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, false, []);

        var fileIndex = index;
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS crawler invoked"),
            OpenIndexAsync = (_, _, _, _) =>
            {
                // After rescan, block is Ready but producer error message is set
                var failedStatus = CreateDriveStatus('X', DriveState.Ready, DriveFailureKind.ProducerFailed,
                    failureMessage: "MFT producer failed during rescan");
                var session = new MftIndexSession(fileIndex, null, ["X"], [], new Dictionary<string, string>())
                {
                    DriveStatusesOverride = () => [new MftDriveStatus("X", failedStatus, null)]
                };
                return Task.FromResult(session);
            },
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
