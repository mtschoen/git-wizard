using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Discovery;

[TestFixture]
public class RepositoryDiscoveryCoordinatorWatchProgressTests
{
    [Test]
    public async Task PrepareWatchReportsPhasesInOrderAndForwardsCatchUpSnapshots()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource();
        var treePath = files.TreePath;
        var session = new MftIndexSession(
            await files.OpenMftIndexAsync(scripted, 'X'),
            null,
            ["X"],
            [new IndexedDrive('X', treePath, 1)],
            new Dictionary<string, string>());
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = _ => new RootClassification(treePath, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled."),
            OpenIndexAsync = (_, _, _, _) => throw new AssertionException("The prepared session must be adopted."),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });
        var request = new DiscoveryRequest(new GitWizardConfiguration { SearchPaths = [treePath] }, DiscoveryPurpose.Watch, false, false, []);
        var reports = new List<WatchStartupProgress>();
        var progress = new SynchronousProgress<WatchStartupProgress>(reports.Add);
        var preparing = coordinator.PrepareWatchAsync(request, session, progress, CancellationToken.None);
        await scripted.WaitForWatchesAsync();
        await AsyncCondition.WaitUntilAsync(() => reports.Any(r => r.Readiness?.TryGet("X", out var x) == true && x.State == VolumeWatchState.CatchingUp));
        await scripted.WatchFor('X').PublishCaughtUpAsync();
        await using var startup = await preparing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(reports.Select(r => r.Phase).Distinct(), Is.EqualTo(new[]
        {
            WatchStartupPhase.Opening, WatchStartupPhase.CatchingUp, WatchStartupPhase.Discovering, WatchStartupPhase.Reconciling
        }));
        Assert.That(reports.Last(r => r.Readiness is not null).Readiness!.CaughtUpVolumes, Is.EqualTo(new[] { "X" }));
        Assert.That(startup.Source, Is.InstanceOf<IVolumeReadiness>());
    }

    [Test]
    public async Task BrokerConnectionFiresConnectingAndConnectedInOrder()
    {
        var events = new List<string>();
        await using var broker = new BlockBrokerFixture();
        var connectAsync = broker.ConnectAsync;
        await using var brokerConnection = new MftBrokerConnection(async cancellationToken =>
        {
            events.Add("connectAsync");
            return await connectAsync(cancellationToken);
        });
        brokerConnection.Connecting += () => events.Add("Connecting");
        brokerConnection.Connected += () => events.Add("Connected");
        var client = await brokerConnection.ConnectAsync(CancellationToken.None);
        Assert.That(client, Is.Not.Null);
        Assert.That(events, Is.EqualTo(new[] { "Connecting", "connectAsync", "Connected" }));
    }

    [Test]
    public async Task WarmSessionDefersBrokerConnectionUntilCatchUpAndReportsElevationPhases()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource();
        var treePath = files.TreePath;
        var connectionFired = false;
        var brokerConnection = new MftBrokerConnection(_ =>
        {
            connectionFired = true;
            return Task.FromResult<MFTLib.BrokerProcess>(null!);
        });
        var session = new MftIndexSession(
            await files.OpenMftIndexAsync(scripted, 'X'),
            null,
            ["X"],
            [new IndexedDrive('X', treePath, 1)],
            new Dictionary<string, string>())
        {
            BrokerConnection = brokerConnection
        };
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = _ => new RootClassification(treePath, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled."),
            OpenIndexAsync = (_, _, _, _) => throw new AssertionException("The prepared session must be adopted."),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });
        var request = new DiscoveryRequest(new GitWizardConfiguration { SearchPaths = [treePath] }, DiscoveryPurpose.Watch, false, false, []);
        var reports = new List<WatchStartupProgress>();
        var progress = new SynchronousProgress<WatchStartupProgress>(reports.Add);

        // Manually trigger broker connection during catch-up to simulate deferred warm-start watch connection
        var started = scripted.WaitForStartAsync('X', CancellationToken.None);
        _ = started.ContinueWith(_ => brokerConnection.ConnectAsync(CancellationToken.None));

        var preparing = coordinator.PrepareWatchAsync(request, session, progress, CancellationToken.None);
        await started.WaitAsync(TimeSpan.FromSeconds(10));
        await AsyncCondition.WaitUntilAsync(() => connectionFired);
        await AsyncCondition.WaitUntilAsync(() => reports.Any(r => r.Phase == WatchStartupPhase.BrokerConnected));
        await scripted.WatchFor('X').PublishCaughtUpAsync();
        await using var startup = await preparing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(reports.Select(r => r.Phase), Does.Contain(WatchStartupPhase.RequestingElevation));
        Assert.That(reports.Select(r => r.Phase), Does.Contain(WatchStartupPhase.BrokerConnected));
    }
}
