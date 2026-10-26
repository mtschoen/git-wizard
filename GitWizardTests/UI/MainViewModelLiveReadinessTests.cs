using System.Runtime.CompilerServices;
using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests.UI;

// Live watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers LiveWatchViewModel.Readiness.cs and StatusHeaderViewModel.cs.
public class MainViewModelLiveReadinessTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;


    sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    [Test]
    public async Task ReadinessDetailsReachTooltipAndCachePanelThroughDispatcher()
    {
        var sourceReady = new TaskCompletionSource<ReadinessVolumeChangeSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new QueuedUiDispatcher();
        var viewModel = TestViewModels.Create(dispatcher: dispatcher);
        var cacheModel = viewModel.IndexCache;
        var tooltipNotifications = new StrongBox<int>(0);
        viewModel.LiveWatch.PropertyChanged += (_, arguments) =>
        {
            if (arguments.PropertyName == nameof(LiveWatchViewModel.LiveReadinessTooltip))
            {
                tooltipNotifications.Value++;
            }
        };
        viewModel.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        viewModel.LiveWatch.PrepareWatchAsync = (request, _, progress, cancellationToken) =>
        {
            var source = new ReadinessVolumeChangeSource();
            progress?.Report(new WatchStartupProgress(WatchStartupPhase.RequestingElevation));
            sourceReady.TrySetResult(source);
            return FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        };
        viewModel.LiveWatch.IsElevated = () => false;
        Task? running = null;
        try
        {
            running = viewModel.LiveWatch.ToggleLiveAsync();
            var source = await sourceReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher.TakeNext()();
            Assert.That(viewModel.LiveWatch.LiveScanProgressText, Is.EqualTo("Requesting elevation..."));
            await source.Started.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher.Drain();
            Assert.That(viewModel.LiveWatch.IsLive, Is.True, "Started still drives IsLive.");
            tooltipNotifications.Value = 0;
            ApplyReadinessTransitions(source, dispatcher, viewModel, cacheModel, tooltipNotifications);
        }
        finally
        {
            try { await viewModel.DisposeAsync(); }
            finally
            {
                if (running is not null)
                {
                    await running;
                }
            }
        }
        dispatcher.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.LiveWatch.LiveVolumes, Is.Empty, "Stopping clears per-drive state.");
            Assert.That(viewModel.LiveWatch.LiveReadinessText, Is.Empty);
            Assert.That(viewModel.LiveWatch.LiveReadinessTooltip, Is.Empty);
            Assert.That(cacheModel.LiveVolumes, Is.Empty);
        }

    }

    static void ApplyReadinessTransitions(
        ReadinessVolumeChangeSource source,
        QueuedUiDispatcher dispatcher,
        MainViewModel viewModel,
        IndexCacheViewModel cacheModel,
        StrongBox<int> tooltipNotifications)
    {
        source.SetVolume(VolumeWatchStatus.CatchingUp("X", VolumeBlockOrigin.Cold));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.LiveWatch.LiveVolumes, Is.Empty, "Readiness must be posted, not applied inline.");
            Assert.That(viewModel.LiveWatch.LiveReadinessTooltip, Is.Empty);
            Assert.That(cacheModel.LiveVolumes, Is.Empty);
            Assert.That(tooltipNotifications.Value, Is.Zero);
        }

        dispatcher.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.LiveWatch.LiveVolumes.Single().DisplayText, Is.EqualTo("X: catching up (cold)"));
            Assert.That(viewModel.LiveWatch.LiveReadinessText, Is.EqualTo("Catching up: X"));
            Assert.That(viewModel.LiveWatch.LiveReadinessTooltip, Is.EqualTo("X: catching up (cold)"));
            Assert.That(cacheModel.LiveVolumes.Single(), Is.SameAs(viewModel.LiveWatch.LiveVolumes.Single()));
            Assert.That(tooltipNotifications.Value, Is.GreaterThan(0));
        }
        var notificationsAfterCatchingUp = tooltipNotifications.Value;

        source.SetVolume(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Cold));
        dispatcher.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.LiveWatch.LiveVolumes.Single().DisplayText, Is.EqualTo("X: caught up (cold)"));
            Assert.That(viewModel.LiveWatch.LiveReadinessText, Is.EqualTo("Ready (journal): X"));
            Assert.That(viewModel.StatusHeader.HeaderText, Is.EqualTo("Live watch active"));
            Assert.That(viewModel.LiveWatch.LiveReadinessTooltip, Is.EqualTo("X: caught up (cold)"));
            Assert.That(cacheModel.LiveVolumes.Single().DisplayText, Is.EqualTo("X: caught up (cold)"));
            Assert.That(tooltipNotifications.Value, Is.GreaterThan(notificationsAfterCatchingUp));
        }

        source.SetVolume(VolumeWatchStatus.Excluded("X", VolumeBlockOrigin.Cold, "watch died"));
        dispatcher.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.LiveWatch.LiveVolumes.Single().DisplayText, Is.EqualTo("X: excluded (cold): watch died"));
            Assert.That(viewModel.LiveWatch.LiveReadinessTooltip, Is.EqualTo("X: excluded (cold): watch died"));
            Assert.That(cacheModel.LiveVolumes.Single().DisplayText, Is.EqualTo("X: excluded (cold): watch died"));
            Assert.That(viewModel.LiveWatch.LiveReadinessText, Is.EqualTo("No caught-up volumes (journal); excluded: X"));
        }

        source.SetVolume(VolumeWatchStatus.CaughtUp("Y", VolumeBlockOrigin.Warm));
        dispatcher.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.LiveWatch.LiveReadinessTooltip, Is.EqualTo(
                "X: excluded (cold): watch died" + Environment.NewLine + "Y: caught up (warm)"));
            Assert.That(viewModel.LiveWatch.LiveReadinessText, Is.EqualTo("Ready (journal): Y; excluded: X"));
            Assert.That(cacheModel.LiveVolumes.Select(row => row.DisplayText), Is.EqualTo(
                ["X: excluded (cold): watch died", "Y: caught up (warm)"]));
        }
    }

    [Test]
    public async Task LiveWatchTransitionsReachTooltipAndPanelThroughDispatcherAndClearOnStop()
    {
        var sourceReady = new TaskCompletionSource<ReadinessVolumeChangeSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new QueuedUiDispatcher();
        var clock = new FixedClock();
        var viewModel = TestViewModels.Create(dispatcher: dispatcher, timeProvider: clock);
        var cacheModel = viewModel.IndexCache;
        viewModel.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        viewModel.LiveWatch.PrepareWatchAsync = (request, _, _, cancellationToken) =>
        {
            var source = new ReadinessVolumeChangeSource();
            sourceReady.TrySetResult(source);
            return FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        };
        viewModel.LiveWatch.IsElevated = () => false;
        Task? running = null;
        try
        {
            running = viewModel.LiveWatch.ToggleLiveAsync();
            var source = await sourceReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.Started.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher.Drain();

            var session = Guid.NewGuid();
            var transition = new VolumeWatchTransition(session, "X", 1, "Faulted", "io error", clock.GetUtcNow());
            var snapshotWithTransition = source.Readiness.WithTransition(transition);

            source.SetSnapshot(snapshotWithTransition);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(viewModel.LiveWatch.LiveWatchEvents, Is.Empty, "History must not mutate before dispatch.");
                Assert.That(cacheModel.LiveWatchEvents, Is.Empty);
            }

            dispatcher.Drain();
            Assert.That(viewModel.LiveWatch.LiveWatchEvents, Has.Count.EqualTo(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(viewModel.LiveWatch.LiveWatchEvents[0].State, Is.EqualTo("Faulted"));
                Assert.That(cacheModel.LiveWatchEvents, Has.Count.EqualTo(1));
                Assert.That(viewModel.LiveWatch.LiveReadinessTooltip, Does.Contain("Recent watch events"));
            }
            Assert.That(viewModel.LiveWatch.LiveReadinessTooltip, Does.Contain("X: Faulted: io error"));
        }
        finally
        {
            try { await viewModel.DisposeAsync(); }
            finally
            {
                if (running is not null)
                {
                    await running;
                }
            }
        }
        dispatcher.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.LiveWatch.LiveWatchEvents, Is.Empty, "Stopping clears transition history.");
            Assert.That(cacheModel.LiveWatchEvents, Is.Empty);
        }
    }
}
