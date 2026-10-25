using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests.UI;

// Covers LiveWatchViewModel.cs, RefreshCoordinator.cs, and MainViewModel.cs.
public class LiveWatchViewModelCompositionTests
{
    [Test]
    public async Task RefreshIsRefreshingChange_RaisesCanToggleLiveAndLiveTooltip()
    {
        await using var viewModel = TestViewModels.Create();
        viewModel.LiveWatch.CapabilityProbe = () =>
            new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        var liveNames = new List<string?>();
        var shellNames = new List<string?>();
        viewModel.LiveWatch.PropertyChanged += (_, arguments) =>
            liveNames.Add(arguments.PropertyName);
        viewModel.PropertyChanged += (_, arguments) =>
            shellNames.Add(arguments.PropertyName);

        viewModel.Refresh.IsRefreshing = true;
        Assert.Multiple(() =>
        {
            Assert.That(liveNames, Is.EqualTo([
                nameof(LiveWatchViewModel.CanToggleLive),
                nameof(LiveWatchViewModel.LiveTooltip)
            ]));
            Assert.That(viewModel.LiveWatch.CanToggleLive, Is.False);
            Assert.That(shellNames, Is.Empty);
        });

        await viewModel.LiveWatch.DisposeAsync();
        liveNames.Clear();
        viewModel.Refresh.IsRefreshing = false;
        Assert.That(liveNames, Is.Empty, "Disposal detaches the refresh subscription.");
    }

    [Test]
    public async Task LiveCreated_WhileRefreshStreams_AppendsWithoutPositionalInsert()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var dispatcher = new QueuedUiDispatcher();
        await using var viewModel = TestViewModels.Create(
            dispatcher: dispatcher, timeProvider: new ControlledDelayClock());
        var laterPath = fixture.Path + "-zulu";
        viewModel.RepositoryList.AddRepository(new GitWizardRepository(laterPath), streaming: false);
        var streamedRows = viewModel.RepositoryList.Repositories;
        var inserts = new PositionalInsertRecorder(streamedRows);

        await viewModel.LiveWatch.ApplyLiveEventAsync(
            new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));
        Assert.That(streamedRows, Has.Count.EqualTo(1), "The add is still queued.");
        viewModel.Refresh.IsRefreshing = true;
        viewModel.Refresh.IsScanning = true;
        dispatcher.Drain();
        Assert.Multiple(() =>
        {
            Assert.That(inserts.Count, Is.Zero);
            Assert.That(streamedRows.Select(row => row.WorkingDirectory),
                Is.EqualTo([laterPath, fixture.Path]));
            Assert.That(viewModel.Refresh.IsScanning, Is.True);
        });

        viewModel.Refresh.CompleteRefresh();
        dispatcher.Drain();
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.RepositoryList.Repositories, Is.Not.SameAs(streamedRows));
            Assert.That(viewModel.RepositoryList.Repositories.Select(row => row.WorkingDirectory),
                Is.EqualTo([fixture.Path, laterPath]));
            Assert.That(viewModel.Refresh.IsRefreshing, Is.False);
            Assert.That(viewModel.Refresh.IsScanning, Is.False);
        });
    }

    [Test]
    public async Task IndexCacheLiveVolumes_SharesLiveWatchCollections()
    {
        var clock = new ControlledDelayClock();
        await using var viewModel = TestViewModels.Create(timeProvider: clock);
        var cache = viewModel.IndexCache;
        var row = new LiveVolumeStatusViewModel(
            VolumeWatchStatus.CatchingUp("C", VolumeBlockOrigin.Cold));
        viewModel.LiveWatch.LiveVolumes.Add(row);
        Assert.That(cache.LiveVolumes.Single(), Is.SameAs(row));
        Assert.That(cache.LiveWatchEvents, Is.Empty);

        var transition = new VolumeWatchTransition(Guid.NewGuid(), "C", 1,
            "Recovering", null, clock.GetUtcNow());
        viewModel.LiveWatch.LiveWatchEvents.Add(transition);
        Assert.That(cache.LiveWatchEvents, Is.EqualTo([transition]));
        row.Update(VolumeWatchStatus.CaughtUp("C", VolumeBlockOrigin.Cold));
        Assert.That(cache.LiveVolumes.Single().DisplayText, Is.EqualTo("C: caught up (cold)"));

        viewModel.LiveWatch.LiveVolumes.Clear();
        viewModel.LiveWatch.LiveWatchEvents.Clear();
        Assert.Multiple(() =>
        {
            Assert.That(cache.LiveVolumes, Is.Empty);
            Assert.That(cache.LiveWatchEvents, Is.Empty);
            Assert.That(cache.Snapshot, Is.Null);
            Assert.That(viewModel.LiveWatch.IsLive, Is.False);
        });
    }
}
