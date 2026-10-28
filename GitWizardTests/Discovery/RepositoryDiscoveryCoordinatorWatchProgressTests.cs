using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;
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
            [await files.OpenMftIndexAsync(scripted, 'X')],
            null,
            ["X"],
            new Dictionary<string, string>());
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
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
        await scripted.WaitForStartAsync('X', 1, CancellationToken.None).WaitAsync(TestTimeouts.HangGuard);
        await AsyncCondition.WaitUntilAsync(() => reports.Any(r => r.Readiness?.TryGet("X", out var x) == true && x.State == VolumeWatchState.CatchingUp));
        await scripted.WatchFor('X').PublishCaughtUpAsync();
        await using var startup = await preparing.WaitAsync(TimeSpan.FromSeconds(10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reports.Select(r => r.Phase).Distinct(), Is.EqualTo([
                    WatchStartupPhase.Opening, WatchStartupPhase.CatchingUp, WatchStartupPhase.Discovering, WatchStartupPhase.Reconciling
                ]));
            Assert.That(reports.Last(r => r.Readiness is not null).Readiness!.CaughtUpVolumes, Is.EqualTo(["X"]));
            Assert.That(startup.Source, Is.InstanceOf<IVolumeReadiness>());
        }
    }

    [Test]
    public async Task BrokerSessionFiresConnectingAndConnectedInOrder()
    {
        var events = new List<string>();
        await using var broker = new BlockBrokerFixture();
        broker.GrowUsnJournal = (_, maximumSize, allocationDelta) => new UsnJournalSettings
        {
            MaximumSize = maximumSize,
            AllocationDelta = allocationDelta
        };
        var connectAsync = broker.ConnectAsync;
        await using var session = BrokerTestHarness.CreateSession(async cancellationToken =>
        {
            events.Add("launch");
            return await connectAsync(cancellationToken);
        });
        session.Connecting += () => events.Add("Connecting");
        session.Connected += () => events.Add("Connected");
        // The session's ConnectAsync is internal to MFTLib; a grow is the public first use that launches.
        await session.GrowUsnJournalAsync('X', new UsnJournalSettings { MaximumSize = 4096, AllocationDelta = 512 }, CancellationToken.None).WaitAsync(TestTimeouts.HangGuard);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(events, Is.EqualTo(["Connecting", "launch", "Connected"]));
            Assert.That(broker.ConnectionCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task WarmSessionDefersBrokerConnectionUntilCatchUpAndReportsElevationPhases()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource();
        var treePath = files.TreePath;
        var connectionFired = false;
        await using var broker = new BlockBrokerFixture();
        broker.GrowUsnJournal = (_, maximumSize, allocationDelta) => new UsnJournalSettings
        {
            MaximumSize = maximumSize,
            AllocationDelta = allocationDelta
        };
        var connectAsync = broker.ConnectAsync;
        // Not using-declared: the catch-up trigger below captures the session in a continuation,
        // and the fixture disposes the in-process process it launched.
        var brokerSession = BrokerTestHarness.CreateSession(cancellationToken =>
        {
            connectionFired = true;
            return connectAsync(cancellationToken);
        });
        var session = new MftIndexSession(
            [await files.OpenMftIndexAsync(scripted, 'X')],
            null,
            ["X"],
            new Dictionary<string, string>())
        {
            BrokerSession = brokerSession
        };
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
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

        // Manually trigger the session's first use during catch-up to simulate deferred warm-start watch connection
        var started = scripted.WaitForStartAsync('X', 1, CancellationToken.None);
        _ = started.ContinueWith(_ => brokerSession.GrowUsnJournalAsync('X', new UsnJournalSettings { MaximumSize = 4096, AllocationDelta = 512 }, CancellationToken.None));

        var preparing = coordinator.PrepareWatchAsync(request, session, progress, CancellationToken.None);
        await started.WaitAsync(TestTimeouts.HangGuard);
        await AsyncCondition.WaitUntilAsync(() => connectionFired);
        await AsyncCondition.WaitUntilAsync(() => reports.Any(r => r.Phase == WatchStartupPhase.BrokerConnected));
        await scripted.WatchFor('X').PublishCaughtUpAsync();
        await using var startup = await preparing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(reports.Select(r => r.Phase), Does.Contain(WatchStartupPhase.RequestingElevation));
        Assert.That(reports.Select(r => r.Phase), Does.Contain(WatchStartupPhase.BrokerConnected));
    }
}
