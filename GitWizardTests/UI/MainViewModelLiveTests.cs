// Live event and lifecycle cases share this fixture and its local source doubles.
using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using GitWizardTests.Watch;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests.UI;

/// <summary>
/// Drives <c>MainViewModel</c>'s Live-mode seam directly: <see cref="MainViewModel.ApplyLiveEvent"/>
/// for per-event application (no controller involved), and <see cref="MainViewModel.ToggleLiveAsync"/>
/// with the <see cref="MainViewModel.LiveVolumeChangeSourceFactory"/>/<see cref="MainViewModel.LiveIsElevated"/>
/// testability seams for the start/stop/failure lifecycle - the real Windows factory would spawn a
/// live UAC prompt, which an automated test can never drive.
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
        viewModel.LiveVolumeChangeSourceFactory = () =>
        {
            var source = new DiagnosticVolumeChangeSource();
            sourceReady.TrySetResult(source);
            return source;
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

    #region IsLive / CanToggleLive / ToggleLiveCommand wiring

    [Test]
    public void IsLive_DefaultsFalse_AndCommandIsExecutable()
    {
        var viewModel = NewViewModel();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(viewModel.ToggleLiveCommand.CanExecute(null), Is.True);
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
            Assert.That(viewModel.ToggleLiveCommand.CanExecute(null), Is.False);
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
            Assert.That(viewModel.ToggleLiveCommand.CanExecute(null), Is.False);
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
            Assert.That(viewModel.ToggleLiveCommand.CanExecute(null), Is.True);
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
            Assert.That(viewModel.ToggleLiveCommand.CanExecute(null), Is.True);
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
        viewModel.LiveVolumeChangeSourceFactory = () => source;
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
        viewModel.LiveVolumeChangeSourceFactory = () => source;
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
        viewModel.LiveVolumeChangeSourceFactory = () => throw new InvalidOperationException("declined UAC prompt");
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
        viewModel.LiveVolumeChangeSourceFactory = () => source;
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
        viewModel.LiveVolumeChangeSourceFactory = () => source;
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
        viewModel.LiveVolumeChangeSourceFactory = () => source;
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
            viewModel.LiveVolumeChangeSourceFactory = () => source;
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
    public async Task LiveVolumeChangeSourceFactory_DefaultsToRealPlatformSourceConstructor()
    {
        var viewModel = NewViewModel();

        await using var source = viewModel.LiveVolumeChangeSourceFactory();

        if (OperatingSystem.IsWindows())
            Assert.That(source.GetType().Name, Is.EqualTo("IndexVolumeChangeSource"));
        else if (OperatingSystem.IsLinux())
            Assert.That(source.GetType().Name, Is.EqualTo("FanotifyVolumeChangeSource"));
        else
            Assert.Ignore("Unsupported platform.");
    }

    [Test]
    public async Task LiveVolumeChangeSourceFactory_DefaultsToRealLinuxSourceConstructor()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("The default Live volume-change source on Linux is Linux-only.");

        var viewModel = NewViewModel();

        await using var source = viewModel.LiveVolumeChangeSourceFactory();

        Assert.That(source.GetType().Name, Is.EqualTo("FanotifyVolumeChangeSource"));
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
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
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
        return Task.FromResult(new VolumeArmResult(_errors));
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
