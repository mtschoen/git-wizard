using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Discovery;

[TestFixture]
public class DiscoveryIndexLifetimeTests
{
    [Test]
    public async Task CancellationPropagatesAndDisposesOwner()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();

        var configuration = new GitWizardConfiguration { SearchPaths = { @"C:\repos" } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var owner = fixture.Owner;
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "C", null),
            CrawlRootAsync = (_, _, _) => Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(owner),
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        var scope = DiscoveryScope.CreateExpanded(request, [@"C:\repos"], [], [], dependencies.ClassifyRoot);
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await coordinator.DiscoverScopeAsync(request, scope, null, null, cts.Token));

        Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
    }

    [Test]
    public async Task StatusSnapshotsSurviveResultDisposal()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();

        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.CompleteNonempty,
            Route = DiscoveryRoute.Indexed,
            Paths = [@"C:\repo"],
            RequestedNtfsDrives = ["X"],
            Observations = [new(@"C:\repo", false, true, DiscoverySource.Scan, DateTime.UtcNow, null)],
            DriveStatuses = fixture.Owner.CaptureDriveStatuses(),
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };

        var result = new RepositoryDiscoveryResult(inventory, fixture.Owner);
        await result.DisposeAsync();

        Assert.That(result.Inventory.DriveStatuses, Is.Not.Empty);
        Assert.That(result.Inventory.DriveStatuses.Single().Drive, Is.EqualTo("X"));
    }
}
