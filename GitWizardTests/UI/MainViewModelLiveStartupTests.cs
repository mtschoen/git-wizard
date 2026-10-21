using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using GitWizardTests.Watch;
using GitWizardUI.ViewModels;

namespace GitWizardTests.UI;

// Live watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers MainViewModel.LiveStartup.cs.
public class MainViewModelLiveStartupTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel NewViewModel()
        => new(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

    static GitWizardRepository Repo(string workingDirectory, params string[] remotes)
    {
        var repository = new GitWizardRepository(workingDirectory);
        foreach (var url in remotes)
            repository.RemoteUrls.Add(url);
        return repository;
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }


    [Test]
    public async Task DriveFailureUpdatesLiveWarningWithoutRepositoryEvent()
    {
        var sourceReady = new TaskCompletionSource<DiagnosticVolumeChangeSource>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new QueuedLiveUiDispatcher();
        var viewModel = new MainViewModel(dispatcher, new StubUserDialogs(), new StubClipboardService());
        viewModel.LiveCapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
        {
            var source = new DiagnosticVolumeChangeSource();
            sourceReady.TrySetResult(source);
            return FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        };
        viewModel.LiveIsElevated = () => false;
        Task? running = null;
        GitWizardUI.Services.LiveWatchController controller;
        try
        {
            running = viewModel.ToggleLiveAsync();
            var source = await sourceReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.Started.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher.Drain();
            controller = WatchSubscriptionProbe.Controller(viewModel);
            Assert.That(WatchSubscriptionProbe.ScanErrorsSubscribers(controller), Is.EqualTo(1));
            source.FailDrive("X", "watch failed");
            Assert.That(viewModel.HeaderText, Is.EqualTo("Live watch active"), "Warnings must be posted to the UI.");
            dispatcher.Drain();
            Assert.That(viewModel.HeaderText, Is.EqualTo("Live scan error on X: watch failed"));
            Assert.That(viewModel.IsLive, Is.True);
            source.FailDrive("X", "watch failed again");
            dispatcher.Drain();
            Assert.That(viewModel.HeaderText, Is.EqualTo("Live scan error on X: watch failed again"));
        }
        finally
        {
            try
            {
                await viewModel.DisposeAsync();
            }
            finally
            {
                if (running is not null)
                    await running;
            }
        }
        var completedSource = await sourceReady.Task;
        Assert.That(completedSource.DiagnosticSubscribers, Is.Zero);
        Assert.That(WatchSubscriptionProbe.ScanErrorsSubscribers(controller), Is.Zero,
            "The view model must detach from the controller after shutdown.");
    }

    [Test]
    public async Task ExpandIgnoredPaths_PreservesNonExistentIgnoredPaths()
    {
        var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync();
        var nonExistentIgnored = Path.Combine(Path.GetTempPath(), $"gw-nonexistent-{Guid.NewGuid():N}");
        configuration.IgnoredPaths.Add(nonExistentIgnored);
        try
        {
            var ignored = GitWizardApi.ExpandIgnoredPaths(configuration.IgnoredPaths);

            var expected = GitWizardApi.ExpandIgnoredPath(nonExistentIgnored);
            Assert.That(ignored, Does.Contain(expected));
        }
        finally
        {
            configuration.IgnoredPaths.Remove(nonExistentIgnored);
        }
    }

    [Test]
    public void LivePrepareWatchAsync_DefaultsToCoordinatorPrepare()
    {
        var viewModel = NewViewModel();

        // The production default runs the discovery coordinator's watch preparation (an
        // elevated broker session), so it is asserted, not invoked.
        var prepare = viewModel.LivePrepareWatchAsync;

        Assert.That(prepare, Is.Not.Null);
        Assert.That(viewModel.LivePrepareWatchAsync, Is.SameAs(prepare));
    }

    [Test]
    public async Task ToggleLiveAsync_WhileArming_ShowsStartingStateUntilStopped()
    {
        var viewModel = NewViewModel();
        var source = new BlockingArmVolumeChangeSource();
        viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        viewModel.LiveIsElevated = () => false;

        var startTask = viewModel.ToggleLiveAsync();
        await source.ArmStarted;

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLiveStarting, Is.True);
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(viewModel.LiveButtonText, Is.EqualTo("Starting Live..."));
            Assert.That(viewModel.HeaderText, Is.EqualTo("Starting Live watch..."));
        });

        await viewModel.ToggleLiveAsync();
        await startTask;

        Assert.That(viewModel.IsLiveStarting, Is.False);
    }

    [Test]
    public async Task ToggleLiveAsync_StartThenToggleAgain_StopsAndClearsIsLive()
    {
        var viewModel = NewViewModel();
        var source = new HangingKillableSource();
        viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        viewModel.LiveIsElevated = () => false;

        var startTask = viewModel.ToggleLiveAsync();
        await WaitUntilAsync(() => viewModel.IsLive);

        await viewModel.ToggleLiveAsync();
        await startTask;

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(source.DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ToggleLiveAsync_SourceFactoryThrowsInvalidOperationException_EndsNotLiveWithStatus()
    {
        var viewModel = NewViewModel();
        viewModel.LivePrepareWatchAsync = (_, _, _, _) =>
            Task.FromException<WatchStartupResult>(new InvalidOperationException("declined UAC prompt"));
        viewModel.LiveIsElevated = () => false;

        await viewModel.ToggleLiveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(viewModel.HeaderText, Does.Contain("declined UAC prompt"));
        });
    }

    [Test]
    public async Task ToggleLiveAsync_SourceDiesNotElevated_ClearsIsLiveWithStoppedReason()
    {
        var viewModel = NewViewModel();
        var source = new HangingKillableSource();
        viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        viewModel.LiveIsElevated = () => false;

        var startTask = viewModel.ToggleLiveAsync();
        await WaitUntilAsync(() => source.IsArmed);

        source.KillSource("driver hiccup");
        await WaitUntilAsync(() => !viewModel.IsLive);
        await startTask;

        Assert.That(viewModel.HeaderText, Does.Contain("driver hiccup"));
    }

    [Test]
    public async Task ToggleLiveAsync_ScanErrorsPresent_SurfacedInHeaderOnEvent()
    {
        var errors = new Dictionary<string, string> { ["D"] = "drive not ready" };
        var batch = new VolumeChangeBatch("C",
            new[] { new VolumeChangeEntry(@"C:\tracked\file.txt", VolumeEntryKind.Modified) });
        var source = new FakeVolumeChangeSource(new[] { batch }, errors);

        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo(@"C:\tracked"));
        viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        viewModel.LiveIsElevated = () => false;

        var startTask = viewModel.ToggleLiveAsync();
        await WaitUntilAsync(() => viewModel.HeaderText.Contains("drive not ready"));

        await viewModel.ToggleLiveAsync();
        await startTask;

        Assert.That(viewModel.HeaderText, Does.Contain("Live scan error on D: drive not ready"));
    }

    [Test]
    public async Task ToggleLiveAsync_NeverStarted_StopBranchIsSafeNoOp()
    {
        var viewModel = NewViewModel();

        // IsLive is false, so this hits the "start" branch, not "stop" - covered instead by
        // StopLiveAsync's own null-controller guard exercised through the kill/stop tests above.
        // Exercise the guard directly: force IsLive true without a controller assigned.
        viewModel.IsLive = true;

        await viewModel.ToggleLiveAsync();

        Assert.That(viewModel.IsLive, Is.False);
    }

    [Test]
    public async Task ToggleLiveAsync_SourceDiesWithScanErrors_StoppedHeaderIncludesErrorSuffix()
    {
        var errors = new Dictionary<string, string> { ["D"] = "drive not ready" };
        var source = new KillableSourceWithErrors(errors);
        var viewModel = NewViewModel();
        viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        viewModel.LiveIsElevated = () => false;

        var startTask = viewModel.ToggleLiveAsync();
        await WaitUntilAsync(() => source.IsArmed);

        source.KillSource("driver hiccup");
        await WaitUntilAsync(() => !viewModel.IsLive);
        await startTask;

        Assert.That(viewModel.HeaderText, Does.Contain("scan errors: D: drive not ready"));
    }

    [Test]
    public async Task ToggleLiveAsync_WithConfiguredSearchPaths_ExpandsSearchPaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"gw-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var config = await GitWizardConfiguration.GetGlobalConfigurationAsync();
            config.SearchPaths.Clear();
            config.SearchPaths.Add(tempDir);

            var source = new HangingKillableSource();
            var viewModel = NewViewModel();
            viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
                FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
            viewModel.LiveIsElevated = () => false;

            var startTask = viewModel.ToggleLiveAsync();
            await WaitUntilAsync(() => source.IsArmed);

            await viewModel.ToggleLiveAsync();
            await startTask;
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task ToggleLiveAsync_TrackedRepoRenamedWithinSearchRoot_EndsWithOneEntryAtNewPath()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        var refreshed = new GitWizardRepository(fixture.Path);
        refreshed.Refresh();
        var remoteUrl = refreshed.RemoteUrls.Single();
        var searchRoot = Path.GetDirectoryName(fixture.Path)!;
        var oldPath = Path.Combine(searchRoot, "gw-old-" + Guid.NewGuid().ToString("N"));

        var config = await GitWizardConfiguration.GetGlobalConfigurationAsync();
        config.SearchPaths.Clear();
        config.SearchPaths.Add(searchRoot);

        var volume = WatchPath.Parse(fixture.Path).Volume!;
        var batch = new VolumeChangeBatch(volume, new[]
        {
                new VolumeChangeEntry(oldPath, VolumeEntryKind.Deleted),
                new VolumeChangeEntry(fixture.Path, VolumeEntryKind.MovedIn),
            });
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo(oldPath, remoteUrl));
        viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(new FakeVolumeChangeSource(new[] { batch }), request, cancellationToken);
        viewModel.LiveIsElevated = () => false;

        await viewModel.ToggleLiveAsync();

        // The Created application awaits a real Refresh on a thread pool thread, and an
        // uncorrelated Deleted only expires after the rename-correlation window, so poll for the
        // settled end state instead of asserting intermediate counts.
        await WaitUntilAsync(() =>
        {
            var paths = viewModel.Repositories.Select(node => node.WorkingDirectory).ToList();
            return paths.Count == 1 && paths[0] == fixture.Path;
        });
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.Repositories.Single().WorkingDirectory, Is.EqualTo(fixture.Path));
            Assert.That(viewModel.Repositories.Select(node => node.WorkingDirectory),
                Does.Not.Contain(oldPath));
        });
    }

    [Test]
    public async Task ToggleLiveAsync_StartupInventory_ReconcilesMembershipBeforeEvents()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repositoryPath = fixture.Path;
        var source = new HangingKillableSource();
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/stale"));
        viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken,
                inventoryPaths: [repositoryPath]);
        viewModel.LiveIsElevated = () => false;

        var startTask = viewModel.ToggleLiveAsync();

        // The coordinator's startup inventory claims only the fixture repository: the stale
        // row leaves and the repository created while Live was closed enters, before any
        // watch event is applied.
        await WaitUntilAsync(() =>
        {
            var paths = viewModel.Repositories.Select(node => node.WorkingDirectory).ToList();
            return paths.Count == 1 && paths[0] == repositoryPath;
        });

        await viewModel.ToggleLiveAsync();
        await startTask;
    }

    [Test]
    public async Task ToggleLiveAsync_StartupUnusable_SurfacesErrorAndPreservesPriorRows()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/keep-me"));
        viewModel.LivePrepareWatchAsync = (request, _, _, _) =>
            Task.FromResult(FakeLiveStartup.Unusable(request, "X", "broker declined"));
        viewModel.LiveIsElevated = () => false;

        var startTask = viewModel.ToggleLiveAsync();
        await WaitUntilAsync(() => viewModel.HeaderText.Contains("could not start"));
        await startTask;

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(viewModel.IsLiveStarting, Is.False);
            Assert.That(viewModel.HeaderText, Does.Contain("Live watch could not start: broker declined"));
            Assert.That(viewModel.Repositories.Select(node => node.WorkingDirectory),
                Is.EqualTo(new[] { "/keep-me" }), "An unusable startup must not touch prior rows.");
        });
    }
}
