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
            CrawlRootAsync = (_, _, _) => Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(owner),
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };

        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        var scope = DiscoveryScope.CreateExpanded(request, [@"C:\repos"], [], []);
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
            Observations = [new RootObservation(@"C:\repo", false, true, DiscoverySource.Scan, DateTime.UtcNow, null)],
            RootStatuses = fixture.Owner.CaptureRootStatuses(),
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };

        var result = new RepositoryDiscoveryResult(inventory, fixture.Owner);
        await result.DisposeAsync();

        Assert.That(result.Inventory.RootStatuses, Is.Not.Empty);
        Assert.That(result.Inventory.RootStatuses.Single().Drive, Is.EqualTo("X"));
    }

    [Test]
    public async Task PendingSizePass_DisposalCancelsBeforeSessionDisposal()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        var sizeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sizeFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = fixture.Owner;
        var disposalsAtSizeCompletion = -1;
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            CrawlRootAsync = (_, _, _) => Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(owner),
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException(),
            ComputeRepositorySizes = (_, _, cancellationToken) =>
            {
                sizeStarted.TrySetResult();
                try
                {
                    using var cancellationRegistration = cancellationToken.Register(() => releaseSize.TrySetResult());
                    releaseSize.Task.GetAwaiter().GetResult();
                    cancellationToken.ThrowIfCancellationRequested();
                    return new Dictionary<string, long>();
                }
                finally
                {
                    disposalsAtSizeCompletion = owner.IsDisposed ? 1 : 0;
                    sizeFinished.TrySetResult();
                }
            }
        };
        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        var searchRoot = fixture.Owner.Indices.Single().Root().Path;
        var configuration = new GitWizardConfiguration { SearchPaths = { searchRoot } };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);
        var discoveryTask = Task.Run(() => coordinator.DiscoverAsync(request, null, CancellationToken.None));
        RepositoryDiscoveryResult? result = null;
        try
        {
            await sizeStarted.Task.WaitAsync(TestTimeouts.HangGuard);
            result = await discoveryTask.WaitAsync(TestTimeouts.HangGuard);
            var disposalTask = result.DisposeAsync().AsTask();
            await sizeFinished.Task.WaitAsync(TestTimeouts.HangGuard);
            await disposalTask.WaitAsync(TestTimeouts.HangGuard);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(disposalsAtSizeCompletion, Is.Zero);
                Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
            }
        }
        finally
        {
            releaseSize.TrySetResult();
            result ??= await discoveryTask.WaitAsync(TestTimeouts.HangGuard);
            await result.DisposeAsync();
        }
    }
}
