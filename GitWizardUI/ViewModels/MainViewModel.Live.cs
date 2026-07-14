using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GitWizard;
using GitWizard.Watch;
using GitWizardUI.Services;
using MFTLib;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    // A Deleted(root) is held here, not removed immediately, so a Created event that follows
    // within RenameCorrelationWindowMilliseconds and matches its remote URL can be resolved as a
    // rename instead of an independent delete+add. RepositoryWatchService's own debounce window
    // already coalesces a same-window rename's Deleted+Created into back-to-back events, so this
    // window mainly bridges the rarer case where they land in different debounce flushes.
    const int RenameCorrelationWindowMilliseconds = 300;

    readonly Dictionary<string, (GitWizardRepository Repository, CancellationTokenSource ExpireCts)> _pendingDeletedRepos = new();
    readonly HashSet<string> _reportedLiveScanErrorDrives = new(StringComparer.OrdinalIgnoreCase);

    LiveWatchController? _liveController;

    // Swappable dependencies for testability: the real Windows factory spawns an elevated broker
    // (a live UAC prompt), which an automated test can never drive. Tests substitute a fake
    // IVolumeChangeSource / elevation answer here to exercise ToggleLiveAsync end-to-end.
    internal Func<IVolumeChangeSource> LiveVolumeChangeSourceFactory { get; set; } = CreateDefaultVolumeChangeSourceFactory();
    internal Func<bool> LiveIsElevated { get; set; } = ElevationUtilities.IsElevated;

    internal void ApplyLiveEvent(RepositoryChangeEvent ev)
    {
        switch (ev.Kind)
        {
            case RepositoryChangeKind.Changed:
                ApplyLiveChanged(ev.RepoRoot);
                break;
            case RepositoryChangeKind.Created:
                ApplyLiveCreated(ev.RepoRoot);
                break;
            case RepositoryChangeKind.Deleted:
                ApplyLiveDeleted(ev.RepoRoot);
                break;
            case RepositoryChangeKind.Renamed:
                // RepositoryWatchService never emits this kind (see its class doc): rename
                // correlation happens here instead, from a Deleted+Created pair.
                break;
        }
    }

    void ApplyLiveChanged(string repoRoot)
    {
        if (!_repositoryMap.TryGetValue(repoRoot, out var node))
            return;

        node.Repository.Refresh();
        UpdateCompletedRepository(node.Repository);
    }

    void ApplyLiveCreated(string repoRoot)
    {
        var repository = new GitWizardRepository(repoRoot);
        repository.Refresh();

        var renamedFrom = TryCorrelateRename(repoRoot, repository);
        if (renamedFrom != null)
            RemoveRepositoryByPath(renamedFrom);

        AddRepository(repository);
    }

    void ApplyLiveDeleted(string repoRoot)
    {
        if (!_repositoryMap.TryGetValue(repoRoot, out var node))
            return;

        var expireCts = new CancellationTokenSource();
        _pendingDeletedRepos[repoRoot] = (node.Repository, expireCts);
        _ = ExpirePendingDeleteAsync(repoRoot, expireCts.Token);
    }

    // Checks every still-pending Deleted against the newly Created repo's remote URLs (reusing
    // the same FindRenamedRepo the batch-refresh path uses), cancels the matched entry's expiry
    // timer, and returns the old path so the caller can remove it - or null if this Created is
    // unrelated to any pending delete.
    string? TryCorrelateRename(string repoRoot, GitWizardRepository repository)
    {
        var healthyRepos = new Dictionary<string, GitWizardRepository> { [repoRoot] = repository };
        foreach (var (oldPath, pending) in _pendingDeletedRepos)
        {
            if (FindRenamedRepo(pending.Repository, oldPath, healthyRepos) != repoRoot)
                continue;

            pending.ExpireCts.Cancel();
            pending.ExpireCts.Dispose();
            _pendingDeletedRepos.Remove(oldPath);
            return oldPath;
        }

        return null;
    }

    async Task ExpirePendingDeleteAsync(string repoRoot, CancellationToken token)
    {
        try
        {
            await Task.Delay(RenameCorrelationWindowMilliseconds, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Correlated into a rename (or superseded) before the window elapsed.
            return;
        }

        _ui.Post(() =>
        {
            if (_pendingDeletedRepos.Remove(repoRoot))
                RemoveRepositoryByPath(repoRoot);
        });
    }

    internal async Task ToggleLiveAsync()
    {
        if (IsLive)
        {
            await StopLiveAsync().ConfigureAwait(false);
            return;
        }

        _reportedLiveScanErrorDrives.Clear();
        var trackedRoots = _allRepositories
            .Select(node => node.WorkingDirectory)
            .Where(path => !string.IsNullOrEmpty(path))
            .ToList();
        var searchRoots = GitWizardConfiguration.GetGlobalConfiguration().SearchPaths.ToList();

        var controller = new LiveWatchController(
            LiveVolumeChangeSourceFactory,
            trackedRoots,
            searchRoots,
            onEvent: ev => _ui.Post(() =>
            {
                SurfaceNewLiveScanErrors(RequireLiveController());
                ApplyLiveEvent(ev);
            }),
            onStopped: reason => _ui.Post(() =>
            {
                IsLive = false;
                HeaderText = $"Live watch stopped: {reason}{FormatLiveScanErrorSuffix(RequireLiveController())}";
            }),
            isElevated: LiveIsElevated);
        _liveController = controller;

        IsLive = true;

        try
        {
            await controller.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            await controller.StopAsync().ConfigureAwait(false);
            IsLive = false;
            HeaderText = $"Live watch could not start: {exception.Message}";
        }
    }

    async Task StopLiveAsync()
    {
        if (_liveController != null)
            await _liveController.StopAsync().ConfigureAwait(false);

        IsLive = false;
    }

    // onEvent/onStopped only ever run after ToggleLiveAsync has assigned _liveController (they
    // are wired as part of the same construction, and neither fires until the controller starts
    // running asynchronously afterward) - this is an invariant assertion, not a defensive no-op.
    LiveWatchController RequireLiveController() =>
        _liveController ?? throw new InvalidOperationException(
            "_liveController must be assigned before a Live event/stop callback can run.");

    void SurfaceNewLiveScanErrors(LiveWatchController controller)
    {
        foreach (var (drive, message) in controller.ScanErrors)
        {
            if (_reportedLiveScanErrorDrives.Add(drive))
                HeaderText = $"Live scan error on {drive}: {message}";
        }
    }

    static string FormatLiveScanErrorSuffix(LiveWatchController controller)
    {
        if (controller.ScanErrors.Count == 0)
            return string.Empty;

        var errors = string.Join("; ", controller.ScanErrors.Select(pair => $"{pair.Key}: {pair.Value}"));
        return $" (scan errors: {errors})";
    }

    // Windows is the only platform with an IVolumeChangeSource today (Phase 3 adds Linux
    // fanotify); the RuntimeInformation guard lets the platform-compatibility analyzer accept
    // the reference to the windows-only constructor below, mirroring GitWizardApi.Discovery.cs's
    // BrokerScanAsync guard.
    static Func<IVolumeChangeSource> CreateDefaultVolumeChangeSourceFactory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return CreateWindowsVolumeChangeSource;

        return () => throw new PlatformNotSupportedException("Live mode currently requires Windows.");
    }

    [SupportedOSPlatform("windows")]
    static IVolumeChangeSource CreateWindowsVolumeChangeSource() => new UsnVolumeChangeSource(BrokerLauncher.Launch);
}
