using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;

namespace GitWizardTests.UI;

// Covers the unelevated Linux Live path end to end below the pkexec process boundary: the real
// coordinator arms an ElevatedHelperVolumeChangeSource whose helper process is in-memory pipes.
[NonParallelizable]
public class MainViewModelLiveElevationTests
{
    readonly List<HelperPipes> _helpers = [];
    int _launches;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _launches = 0;
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var helper in _helpers)
        {
            await helper.DisposeAsync();
        }

        _helpers.Clear();
    }

    LinuxWatchHelperProcess Launch(Action<HelperPipes> playHelper)
    {
        _launches++;
        var helper = new HelperPipes();
        _helpers.Add(helper);
        playHelper(helper);
        return helper.Process;
    }

    RepositoryDiscoveryCoordinator CreateCoordinator(Action<HelperPipes> playHelper) => new(new RepositoryDiscoveryDependencies
    {
        IsWindows = false,
        IsLinux = true,
        CrawlRootAsync = (_, _, _) => Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())),
        OpenIndexAsync = (_, _, _, _) => throw new PlatformNotSupportedException(),
        ReadListAsync = _ => Task.FromResult<string[]?>([]),
        WriteListAsync = (_, _) => Task.CompletedTask,
        CandidateExists = (_, _) => false,
        CreateLinuxSource = () => new ElevatedHelperVolumeChangeSource(
            LinuxWatchHelperLaunch.CreatePkexecStartInfo("/opt/git-wizard/GitWizardUI"),
            _ => Launch(playHelper), TimeProvider.System, TestTimeouts.ShortHangGuard, _ => false)
    });

    static DiscoveryRequest WatchRequest(string root) =>
        new(new GitWizardConfiguration { SearchPaths = { root } }, DiscoveryPurpose.Watch, false, false, []);

    [Test]
    public async Task DeclinedAuthentication_LeavesLiveOffWithTheWordedMessage_AndPromptsOnce()
    {
        using var root = new TempDirectory("gw-live-elevation-");
        var rootPath = root.Path;
        var coordinator = CreateCoordinator(helper => _ = helper.ExitAsync(126));
        var viewModel = TestViewModels.Create();
        viewModel.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.RequiresElevation, 1);
        viewModel.LiveWatch.IsElevated = () => false;
        var prepareCalls = 0;
        viewModel.LiveWatch.PrepareWatchAsync = (_, prepared, progress, cancellationToken) =>
        {
            prepareCalls++;
            return coordinator.PrepareWatchAsync(WatchRequest(rootPath), prepared, progress, cancellationToken);
        };

        await viewModel.LiveWatch.ToggleLiveAsync().WaitAsync(TestTimeouts.HangGuard);
        await AsyncCondition.WaitUntilAsync(() => viewModel.StatusHeader.HeaderText.Contains("could not start"),
            TestTimeouts.ShortHangGuard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.StatusHeader.HeaderText,
                Is.EqualTo($"Live watch could not start: {ElevatedHelperVolumeChangeSource.AuthenticationCancelledMessage}"));
            Assert.That(viewModel.LiveWatch.IsLive, Is.False);
            Assert.That(viewModel.LiveWatch.IsLiveStarting, Is.False);
            Assert.That(_launches, Is.EqualTo(1), "A declined prompt must not be followed by a second one.");
            Assert.That(prepareCalls, Is.EqualTo(1), "A declined prompt must not respawn the watch.");
        }
    }

    [Test]
    public async Task UnelevatedLinuxWatchStartup_ReportsTheElevationRequestBeforeDiscovery()
    {
        using var root = new TempDirectory("gw-live-elevation-");
        var coordinator = CreateCoordinator(helper => _ = PlayHelperAsync(helper));
        var phases = new List<WatchStartupPhase>();

        await using var startup = await coordinator.PrepareWatchAsync(WatchRequest(root.Path), null,
            new SynchronousProgress<WatchStartupProgress>(report => phases.Add(report.Phase)), CancellationToken.None)
            .WaitAsync(TestTimeouts.HangGuard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(phases, Is.EqualTo(
            [
                WatchStartupPhase.Opening, WatchStartupPhase.RequestingElevation, WatchStartupPhase.BrokerConnected,
                WatchStartupPhase.Discovering
            ]));
            Assert.That(_launches, Is.EqualTo(1));
        }

        static async Task PlayHelperAsync(HelperPipes helper)
        {
            await helper.SendFrameAsync(new HelloFrame(LinuxWatchHelperProtocol.Version));
            var arm = (ArmRequestFrame)(await helper.ReadRequestAsync())!;
            await helper.SendFrameAsync(new ReadinessFrame([.. arm.Roots.Select(path => VolumeWatchStatus.CaughtUp(path, VolumeBlockOrigin.Unknown))]));
            await helper.SendFrameAsync(new ArmResultFrame(new Dictionary<string, string>()));
            while (await helper.ReadRequestAsync() is not null)
            {
                // The helper serves until its stdin closes, then exits.
            }

            await helper.ExitAsync(0);
        }
    }
}
