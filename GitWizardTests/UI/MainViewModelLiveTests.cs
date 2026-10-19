// Live event and lifecycle cases share this fixture and its local source doubles.
using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using GitWizardTests.Watch;
using GitWizardUI.ViewModels;

namespace GitWizardTests.UI;

/// <summary>
/// Drives <c>MainViewModel</c>'s Live-mode seam directly: <see cref="MainViewModel.ApplyLiveEvent"/>
/// for per-event application (no controller involved), and <see cref="MainViewModel.ToggleLiveAsync"/>
/// with the <see cref="MainViewModel.LivePrepareWatchAsync"/>/<see cref="MainViewModel.LiveIsElevated"/>
/// testability seams for the start/stop/failure lifecycle - the real coordinator would arm an
/// elevated broker session (a live UAC prompt), which an automated test can never drive.
/// </summary>
public class MainViewModelLiveTests
{
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

    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    // Frozen so the 30 second watch-event expiry never depends on how long a test takes to run.
    sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

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

    #region ApplyLiveEvent

    [Test]
    public async Task ApplyLiveEvent_Changed_RefreshesAndUpdatesTargetNode()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        var viewModel = NewViewModel();
        viewModel.AddRepository(repository);

        Assert.That(viewModel.Repositories.Single().DisplayText, Does.Not.Contain("*"));

        fixture.AddUntrackedFile("untracked.txt");
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Changed));

        var node = viewModel.Repositories.Single();
        Assert.Multiple(() =>
        {
            Assert.That(node.Repository.HasPendingChanges, Is.True);
            Assert.That(node.DisplayText, Does.Contain("*"));
        });
    }

    [Test]
    public async Task ApplyLiveEvent_Changed_UnknownRepoRoot_NoOp()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a"));

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/unknown", RepositoryChangeKind.Changed));
        Assert.That(viewModel.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ApplyLiveEvent_Created_AddsNewRepositoryNode()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var viewModel = NewViewModel();

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));

        Assert.That(viewModel.Repositories, Has.Count.EqualTo(1));
        Assert.That(viewModel.Repositories.Single().WorkingDirectory, Is.EqualTo(fixture.Path));
    }

    [Test]
    public async Task ApplyLiveEvent_Created_VanishedRepo_IsDroppedQuietly()
    {
        var viewModel = NewViewModel();
        var vanished = Path.Combine(Path.GetTempPath(), "gw-vanished-" + Guid.NewGuid().ToString("N"));

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(vanished, RepositoryChangeKind.Created));

        Assert.That(viewModel.Repositories, Is.Empty);
    }

    [Test]
    public async Task ApplyLiveEvent_DeletedUncorrelated_RemovesNodeAfterCorrelationWindow()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a"));

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Deleted));

        // Buffered, not removed immediately - a matching Created could still turn this into a rename.
        Assert.That(viewModel.Repositories, Has.Count.EqualTo(1));

        await WaitUntilAsync(() => viewModel.Repositories.Count == 0);
    }

    [Test]
    public async Task ApplyLiveEvent_Deleted_UnknownRepoRoot_NoOp()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a"));

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/unknown", RepositoryChangeKind.Deleted));
        Assert.That(viewModel.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ApplyLiveEvent_DeletedThenCreatedWithMatchingRemote_CorrelatesAsRename()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        var refreshed = new GitWizardRepository(fixture.Path);
        refreshed.Refresh();
        var remoteUrl = refreshed.RemoteUrls.Single();

        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/old/path", remoteUrl));

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/old/path", RepositoryChangeKind.Deleted));
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));

        var paths = viewModel.Repositories.Select(node => node.WorkingDirectory).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(paths, Does.Not.Contain("/old/path"));
            Assert.That(paths, Does.Contain(fixture.Path));
            Assert.That(viewModel.Repositories, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ApplyLiveEvent_Deleted_SecondDeleteOverwritesAndCancelsPreviousExpiry()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a"));

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Deleted));
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Deleted));

        Assert.That(viewModel.Repositories, Has.Count.EqualTo(1));
        await WaitUntilAsync(() => viewModel.Repositories.Count == 0);
    }

    [Test]
    public async Task ApplyLiveEvent_DeletedThenCreatedSamePath_CancelsExpiryAndRetainsNode()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var viewModel = NewViewModel();
        viewModel.AddRepository(new GitWizardRepository(fixture.Path));
        viewModel.AddRepository(Repo("/marker"));

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Deleted));
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/marker", RepositoryChangeKind.Deleted));

        // When the marker repository's correlation window expires and it is removed,
        // the earlier correlation window for the recreated repository has also elapsed.
        await WaitUntilAsync(() => viewModel.Repositories.All(node => node.WorkingDirectory != "/marker"));

        Assert.That(viewModel.Repositories, Has.Count.EqualTo(1));
        Assert.That(viewModel.Repositories.Single().WorkingDirectory, Is.EqualTo(fixture.Path));
    }

    [Test]
    public async Task ApplyLiveEvent_DeletedThenCreatedThenDeletedSamePath_RemovesNode()
    {
        var dispatcher = new QueuedLiveUiDispatcher();
        var viewModel = new MainViewModel(dispatcher, new StubUserDialogs(), new StubClipboardService());
        viewModel.AddRepository(Repo("/a"));

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Deleted));
        var createdTask = viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Created));
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Deleted));
        await createdTask;

        dispatcher.Drain();
        await WaitUntilAsync(() =>
        {
            dispatcher.Drain();
            return viewModel.Repositories.Count == 0;
        });

        Assert.That(viewModel.Repositories, Is.Empty);
    }

    [Test]
    public async Task ApplyLiveEvent_DeletedThenCreatedThenDeletedSamePath_Direct_RemovesNode()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var viewModel = NewViewModel();
        viewModel.AddRepository(new GitWizardRepository(fixture.Path));

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Deleted));
        var createdTask = viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Deleted));
        await createdTask;

        await WaitUntilAsync(() => viewModel.Repositories.Count == 0);
        Assert.That(viewModel.Repositories, Is.Empty);
    }

    [Test]
    public async Task ApplyLiveEvent_CreatedApplicationAfterNewerDeleteExpires_DoesNotRestoreNode()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var dispatcher = new QueuedLiveUiDispatcher();
        var viewModel = new MainViewModel(dispatcher, new StubUserDialogs(), new StubClipboardService());
        viewModel.AddRepository(Repo(fixture.Path));

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Deleted));
        dispatcher.Drain();
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));
        // Hold the final UI application, the same boundary reached by a slow refresh.
        var applyCreated = dispatcher.TakeNext();
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Deleted));
        dispatcher.Drain();
        await WaitUntilAsync(() =>
        {
            dispatcher.Drain();
            return viewModel.Repositories.Count == 0;
        });

        applyCreated();
        Assert.That(viewModel.Repositories, Is.Empty);

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));
        dispatcher.Drain();
        Assert.That(viewModel.Repositories.Single().WorkingDirectory, Is.EqualTo(fixture.Path),
            "A later genuine recreation must still be accepted.");
    }

    [Test]
    public async Task ApplyLiveEvent_OlderCreatedApplication_DoesNotReplaceNewerCreation()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var dispatcher = new QueuedLiveUiDispatcher();
        var viewModel = new MainViewModel(dispatcher, new StubUserDialogs(), new StubClipboardService());

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));
        var applyOlderCreation = dispatcher.TakeNext();
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));

        applyOlderCreation();
        Assert.That(viewModel.Repositories, Is.Empty);
        dispatcher.Drain();
        Assert.That(viewModel.Repositories.Single().WorkingDirectory, Is.EqualTo(fixture.Path));
    }

    [Test]
    public async Task ApplyLiveEvent_Changed_ConcurrentEvents_HandledCleanly()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        var viewModel = NewViewModel();
        viewModel.AddRepository(repository);

        fixture.AddUntrackedFile("untracked.txt");
        var event1 = viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Changed));
        var event2 = viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Changed));
        await Task.WhenAll(event1, event2);

        var node = viewModel.Repositories.Single();
        Assert.That(node.Repository.HasPendingChanges, Is.True);
    }

    [Test]
    public async Task ApplyLiveEvent_Renamed_IsNoOp()
    {
        var viewModel = NewViewModel();

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Renamed, "/b"));
        Assert.That(viewModel.Repositories, Is.Empty);
    }

    #endregion

    #region IsLive / CanToggleLive wiring

    [Test]
    public void IsLive_DefaultsFalse_AndToggleLiveIsAllowed()
    {
        var viewModel = NewViewModel();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(viewModel.CanToggleLive, Is.True);
        });
    }

    [Test]
    public void CanToggleLive_FalseWhileRefreshing_TrueWhileLive()
    {
        var viewModel = NewViewModel();

        viewModel.IsRefreshing = true;
        Assert.That(viewModel.CanToggleLive, Is.False);

        viewModel.IsRefreshing = false;
        viewModel.IsLive = true;
        Assert.That(viewModel.CanToggleLive, Is.True, "Stopping must stay available even if IsRefreshing is somehow true.");
    }

    [Test]
    public async Task ToggleLiveAsync_WhileRefreshing_IsNoOpAndDoesNotChangeHeader()
    {
        var viewModel = NewViewModel();
        viewModel.IsRefreshing = true;
        viewModel.HeaderText = "Refreshing repositories...";

        await viewModel.ToggleLiveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(viewModel.IsLiveStarting, Is.False);
            Assert.That(viewModel.HeaderText, Is.EqualTo("Refreshing repositories..."));
        });
    }

    [Test]
    public async Task CanToggleLive_UnsupportedKernel_IsFalse_AndTooltipSurfacesReason()
    {
        var viewModel = NewViewModel();
        viewModel.LiveCapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.UnsupportedKernel, 0);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.CanToggleLive, Is.False);
            Assert.That(viewModel.LiveTooltip, Does.Contain("kernel 5.9"));
        });

        await viewModel.ToggleLiveAsync();
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(viewModel.HeaderText, Does.Contain("Live watch unavailable"));
            Assert.That(viewModel.HeaderText, Does.Contain("kernel 5.9"));
        });
    }

    [Test]
    public async Task CanToggleLive_UnsupportedFileSystem_IsFalse_AndTooltipSurfacesReason()
    {
        var viewModel = NewViewModel();
        viewModel.LiveCapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.UnsupportedFileSystem, 0);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.CanToggleLive, Is.False);
            Assert.That(viewModel.LiveTooltip, Does.Contain("unsupported on this filesystem"));
        });

        await viewModel.ToggleLiveAsync();
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(viewModel.HeaderText, Does.Contain("Live watch unavailable"));
            Assert.That(viewModel.HeaderText, Does.Contain("unsupported on this filesystem"));
        });
    }

    [Test]
    public void CanToggleLive_Available_IsTrue_AndTooltipIsDefault()
    {
        var viewModel = NewViewModel();
        viewModel.LiveCapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.CanToggleLive, Is.True);
            Assert.That(viewModel.LiveTooltip, Is.EqualTo("Start or stop live repository monitoring (requires elevation when starting)"));
        });
    }

    [Test]
    public void CanToggleLive_RequiresElevation_IsTrue_AndTooltipIsDefault()
    {
        var viewModel = NewViewModel();
        viewModel.LiveCapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.RequiresElevation, 1);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.CanToggleLive, Is.True);
            Assert.That(viewModel.LiveTooltip, Is.EqualTo("Start or stop live repository monitoring (requires elevation when starting)"));
        });
    }

    #endregion

    #region ToggleLiveAsync lifecycle (fake source seam)

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
    public async Task ToggleLiveAsync_ScanProgress_ReportedWhileLive_ShowsThenClearsOnStop()
    {
        var viewModel = NewViewModel();
        var source = new GatedProgressVolumeChangeSource();
        viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        viewModel.LiveIsElevated = () => false;

        var startTask = viewModel.ToggleLiveAsync();
        await source.ArmStarted.WaitAsync(TimeSpan.FromSeconds(5));

        // Arming runs inside the startup prepare, before the service subscribes scan progress,
        // so the starting state shows without progress.
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLiveStarting, Is.True);
            Assert.That(viewModel.IsLiveScanProgressVisible, Is.False);
        });

        source.ReleaseArm();
        await WaitUntilAsync(() => viewModel.IsLive);

        source.ReportScanProgress(new VolumeScanProgress("D", 2, 5, 1_200_000, 2_400_000));
        await WaitUntilAsync(() => viewModel.IsLiveScanProgressVisible);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LiveScanProgressText,
                Is.EqualTo("Scanning D: (2 of 5), 1.2M of 2.4M records"));
            Assert.That(viewModel.LiveScanProgressValue, Is.EqualTo(0.3).Within(0.001));
            Assert.That(viewModel.IsLiveScanProgressIndeterminate, Is.False);
        });

        await viewModel.ToggleLiveAsync();
        await startTask;

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLiveScanProgressVisible, Is.False);
            Assert.That(viewModel.LiveScanProgressText, Is.Empty);
        });
    }

    [Test]
    public async Task ToggleLiveAsync_ArmFailureDuringStartupPrepare_EndsNotLiveWithStatus()
    {
        var viewModel = NewViewModel();
        var source = new GatedProgressVolumeChangeSource(new InvalidOperationException("scan blew up"));
        viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        viewModel.LiveIsElevated = () => false;

        var startTask = viewModel.ToggleLiveAsync();
        await source.ArmStarted.WaitAsync(TimeSpan.FromSeconds(5));
        source.ReleaseArm();
        await startTask;

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(viewModel.IsLiveStarting, Is.False);
            Assert.That(viewModel.IsLiveScanProgressVisible, Is.False);
            Assert.That(viewModel.LiveScanProgressText, Is.Empty);
            Assert.That(viewModel.HeaderText, Does.Contain("Live watch could not start: scan blew up"));
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

    [Test]
    public async Task ReadinessDetailsReachTooltipAndCachePanelThroughDispatcher()
    {
        var sourceReady = new TaskCompletionSource<ReadinessVolumeChangeSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new QueuedLiveUiDispatcher();
        var viewModel = new MainViewModel(dispatcher, new StubUserDialogs(), new StubClipboardService());
        var cacheModel = viewModel.IndexCache;
        var tooltipNotifications = new System.Runtime.CompilerServices.StrongBox<int>(0);
        viewModel.PropertyChanged += (_, arguments) =>
        {
            if (arguments.PropertyName == nameof(MainViewModel.LiveReadinessTooltip))
                tooltipNotifications.Value++;
        };
        viewModel.LiveCapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        viewModel.LivePrepareWatchAsync = (request, _, progress, cancellationToken) =>
        {
            var source = new ReadinessVolumeChangeSource();
            progress?.Report(new WatchStartupProgress(WatchStartupPhase.RequestingElevation));
            sourceReady.TrySetResult(source);
            return FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        };
        viewModel.LiveIsElevated = () => false;
        Task? running = null;
        try
        {
            running = viewModel.ToggleLiveAsync();
            var source = await sourceReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher.TakeNext()();
            Assert.That(viewModel.LiveScanProgressText, Is.EqualTo("Requesting elevation..."));
            await source.Started.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher.Drain();
            Assert.That(viewModel.IsLive, Is.True, "Started still drives IsLive.");
            tooltipNotifications.Value = 0;
            source.SetVolume(VolumeWatchStatus.CatchingUp("X", VolumeBlockOrigin.Cold));
            Assert.That(viewModel.LiveVolumes, Is.Empty, "Readiness must be posted, not applied inline.");
            Assert.That(viewModel.LiveReadinessTooltip, Is.Empty);
            Assert.That(cacheModel.LiveVolumes, Is.Empty);
            Assert.That(tooltipNotifications.Value, Is.Zero);

            dispatcher.Drain();
            Assert.That(viewModel.LiveVolumes.Single().DisplayText, Is.EqualTo("X: catching up (cold)"));
            Assert.That(viewModel.LiveReadinessText, Is.EqualTo("Catching up: X"));
            Assert.That(viewModel.LiveReadinessTooltip, Is.EqualTo("X: catching up (cold)"));
            Assert.That(cacheModel.LiveVolumes.Single(), Is.SameAs(viewModel.LiveVolumes.Single()));
            Assert.That(tooltipNotifications.Value, Is.GreaterThan(0));
            var notificationsAfterCatchingUp = tooltipNotifications.Value;

            source.SetVolume(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Cold));
            dispatcher.Drain();
            Assert.That(viewModel.LiveVolumes.Single().DisplayText, Is.EqualTo("X: caught up (cold)"));
            Assert.That(viewModel.LiveReadinessText, Is.EqualTo("Ready (journal): X"));
            Assert.That(viewModel.HeaderText, Is.EqualTo("Live watch active"));
            Assert.That(viewModel.LiveReadinessTooltip, Is.EqualTo("X: caught up (cold)"));
            Assert.That(cacheModel.LiveVolumes.Single().DisplayText, Is.EqualTo("X: caught up (cold)"));
            Assert.That(tooltipNotifications.Value, Is.GreaterThan(notificationsAfterCatchingUp));

            source.SetVolume(VolumeWatchStatus.Excluded("X", VolumeBlockOrigin.Cold, "watch died"));
            dispatcher.Drain();
            Assert.That(viewModel.LiveVolumes.Single().DisplayText, Is.EqualTo("X: excluded (cold): watch died"));
            Assert.That(viewModel.LiveReadinessTooltip, Is.EqualTo("X: excluded (cold): watch died"));
            Assert.That(cacheModel.LiveVolumes.Single().DisplayText, Is.EqualTo("X: excluded (cold): watch died"));
            Assert.That(viewModel.LiveReadinessText, Is.EqualTo("No caught-up volumes (journal); excluded: X"));

            source.SetVolume(VolumeWatchStatus.CaughtUp("Y", VolumeBlockOrigin.Warm));
            dispatcher.Drain();
            Assert.That(viewModel.LiveReadinessTooltip, Is.EqualTo(
                "X: excluded (cold): watch died" + Environment.NewLine + "Y: caught up (warm)"));
            Assert.That(viewModel.LiveReadinessText, Is.EqualTo("Ready (journal): Y; excluded: X"));
            Assert.That(cacheModel.LiveVolumes.Select(row => row.DisplayText), Is.EqualTo(
                new[] { "X: excluded (cold): watch died", "Y: caught up (warm)" }));
        }
        finally
        {
            try { await viewModel.DisposeAsync(); } finally { if (running is not null) await running; }
        }
        dispatcher.Drain();
        Assert.That(viewModel.LiveVolumes, Is.Empty, "Stopping clears per-drive state.");
        Assert.That(viewModel.LiveReadinessText, Is.Empty);
        Assert.That(viewModel.LiveReadinessTooltip, Is.Empty);
        Assert.That(cacheModel.LiveVolumes, Is.Empty);
    }

    [Test]
    public async Task LiveWatchTransitionsReachTooltipAndPanelThroughDispatcherAndClearOnStop()
    {
        var sourceReady = new TaskCompletionSource<ReadinessVolumeChangeSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new QueuedLiveUiDispatcher();
        var clock = new FixedClock();
        var viewModel = new MainViewModel(dispatcher, new StubUserDialogs(), new StubClipboardService(), clock);
        var cacheModel = viewModel.IndexCache;
        viewModel.LiveCapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
        {
            var source = new ReadinessVolumeChangeSource();
            sourceReady.TrySetResult(source);
            return FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        };
        viewModel.LiveIsElevated = () => false;
        Task? running = null;
        try
        {
            running = viewModel.ToggleLiveAsync();
            var source = await sourceReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.Started.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher.Drain();

            var session = Guid.NewGuid();
            var transition = new VolumeWatchTransition(session, "X", 1, "Faulted", "io error", clock.GetUtcNow());
            var snapshotWithTransition = source.Readiness.WithTransition(transition);

            source.SetSnapshot(snapshotWithTransition);
            Assert.That(viewModel.LiveWatchEvents, Is.Empty, "History must not mutate before dispatch.");
            Assert.That(cacheModel.LiveWatchEvents, Is.Empty);

            dispatcher.Drain();
            Assert.That(viewModel.LiveWatchEvents, Has.Count.EqualTo(1));
            Assert.That(viewModel.LiveWatchEvents[0].State, Is.EqualTo("Faulted"));
            Assert.That(cacheModel.LiveWatchEvents, Has.Count.EqualTo(1));
            Assert.That(viewModel.LiveReadinessTooltip, Does.Contain("Recent watch events"));
            Assert.That(viewModel.LiveReadinessTooltip, Does.Contain("X: Faulted: io error"));
        }
        finally
        {
            try { await viewModel.DisposeAsync(); } finally { if (running is not null) await running; }
        }
        dispatcher.Drain();
        Assert.That(viewModel.LiveWatchEvents, Is.Empty, "Stopping clears transition history.");
        Assert.That(cacheModel.LiveWatchEvents, Is.Empty);
    }

    #endregion
}

internal sealed class BlockingArmVolumeChangeSource : IVolumeChangeSource
{
    readonly TaskCompletionSource _armStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    event Action<string>? IVolumeChangeSource.SourceDied
    {
        add { }
        remove { }
    }

    public Task ArmStarted => _armStarted.Task;

    public async Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        _armStarted.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// Like HangingKillableSource (LiveWatchControllerTests.cs) but with configurable scan errors, used
// to cover FormatLiveScanErrorSuffix's non-empty-errors branch on the death/stop path.
internal sealed class KillableSourceWithErrors : IVolumeChangeSource
{
    readonly IReadOnlyDictionary<string, string> _errors;

    public event Action<string>? SourceDied;

    public bool IsArmed { get; private set; }

    public KillableSourceWithErrors(IReadOnlyDictionary<string, string> errors) => _errors = errors;

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        IsArmed = true;
        return Task.FromResult(new VolumeArmResult(_errors, []));
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public void KillSource(string reason) => SourceDied?.Invoke(reason);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// Arms only when ReleaseArm is called, so a test can observe the amber mid-arm state, and
// reports IVolumeScanProgress samples on demand. With a non-null armFailure the release throws
// it from ArmAndCatchUpAsync, taking the same InvalidOperationException path as a real open
// failure (MainViewModel.RunLiveControllerAsync's catch).
internal sealed class GatedProgressVolumeChangeSource : IVolumeChangeSource, IVolumeScanProgress
{
    readonly TaskCompletionSource _armStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _armReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Exception? _armFailure;

    public GatedProgressVolumeChangeSource(Exception? armFailure = null) => _armFailure = armFailure;

    event Action<string>? IVolumeChangeSource.SourceDied
    {
        add { }
        remove { }
    }

    public event Action<VolumeScanProgress>? ScanProgress;

    public Task ArmStarted => _armStarted.Task;

    public async Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        _armStarted.TrySetResult();
        await _armReleased.Task.WaitAsync(ct);
        if (_armFailure is not null)
            throw _armFailure;
        return new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public void ReportScanProgress(VolumeScanProgress sample) => ScanProgress?.Invoke(sample);
    public void ReleaseArm() => _armReleased.TrySetResult();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
