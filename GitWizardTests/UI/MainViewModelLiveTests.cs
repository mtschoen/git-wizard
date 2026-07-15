using GitWizard;
using GitWizard.Watch;
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
    public void ApplyLiveEvent_Changed_RefreshesAndUpdatesTargetNode()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        var viewModel = NewViewModel();
        viewModel.AddRepository(repository);

        Assert.That(viewModel.Repositories.Single().DisplayText, Does.Not.Contain("*"));

        fixture.AddUntrackedFile("untracked.txt");
        viewModel.ApplyLiveEvent(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Changed));

        var node = viewModel.Repositories.Single();
        Assert.Multiple(() =>
        {
            Assert.That(node.Repository.HasPendingChanges, Is.True);
            Assert.That(node.DisplayText, Does.Contain("*"));
        });
    }

    [Test]
    public void ApplyLiveEvent_Changed_UnknownRepoRoot_NoOp()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a"));

        Assert.DoesNotThrow(() =>
            viewModel.ApplyLiveEvent(new RepositoryChangeEvent("/unknown", RepositoryChangeKind.Changed)));
        Assert.That(viewModel.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void ApplyLiveEvent_Created_AddsNewRepositoryNode()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var viewModel = NewViewModel();

        viewModel.ApplyLiveEvent(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));

        Assert.That(viewModel.Repositories, Has.Count.EqualTo(1));
        Assert.That(viewModel.Repositories.Single().WorkingDirectory, Is.EqualTo(fixture.Path));
    }

    [Test]
    public Task ApplyLiveEvent_DeletedUncorrelated_RemovesNodeAfterCorrelationWindow()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a"));

        viewModel.ApplyLiveEvent(new RepositoryChangeEvent("/a", RepositoryChangeKind.Deleted));

        // Buffered, not removed immediately - a matching Created could still turn this into a rename.
        Assert.That(viewModel.Repositories, Has.Count.EqualTo(1));

        return WaitUntilAsync(() => viewModel.Repositories.Count == 0);
    }

    [Test]
    public void ApplyLiveEvent_Deleted_UnknownRepoRoot_NoOp()
    {
        var viewModel = NewViewModel();

        Assert.DoesNotThrow(() =>
            viewModel.ApplyLiveEvent(new RepositoryChangeEvent("/unknown", RepositoryChangeKind.Deleted)));
    }

    [Test]
    public void ApplyLiveEvent_DeletedThenCreatedWithMatchingRemote_CorrelatesAsRename()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        var refreshed = new GitWizardRepository(fixture.Path);
        refreshed.Refresh();
        var remoteUrl = refreshed.RemoteUrls.Single();

        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/old/path", remoteUrl));

        viewModel.ApplyLiveEvent(new RepositoryChangeEvent("/old/path", RepositoryChangeKind.Deleted));
        viewModel.ApplyLiveEvent(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));

        var paths = viewModel.Repositories.Select(node => node.WorkingDirectory).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(paths, Does.Not.Contain("/old/path"));
            Assert.That(paths, Does.Contain(fixture.Path));
            Assert.That(viewModel.Repositories, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void ApplyLiveEvent_Renamed_IsNoOp()
    {
        var viewModel = NewViewModel();

        Assert.DoesNotThrow(() =>
            viewModel.ApplyLiveEvent(new RepositoryChangeEvent("/a", RepositoryChangeKind.Renamed, "/b")));
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
        var source = new FakeVolumeChangeSource(Array.Empty<VolumeColdRecord>(), new[] { batch }, errors);

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
    public async Task LiveVolumeChangeSourceFactory_DefaultsToRealWindowsSourceConstructor()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("The default Live volume-change source is Windows-only.");

        var viewModel = NewViewModel();

        await using var source = viewModel.LiveVolumeChangeSourceFactory();

        // GetType().Name (not a static reference to the [SupportedOSPlatform("windows")] type)
        // so this assertion itself stays platform-compat-analyzer clean under the IsWindows guard.
        Assert.That(source.GetType().Name, Is.EqualTo("UsnVolumeChangeSource"));
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
            Array.Empty<VolumeColdRecord>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
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
        return Task.FromResult(new VolumeArmResult(Array.Empty<VolumeColdRecord>(), _errors));
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
