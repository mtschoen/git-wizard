using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardUI.Services;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    // A Deleted(root) is held here, not removed immediately, so a Created event that follows
    // within RenameCorrelationWindowMilliseconds and matches its remote URL can be resolved as a
    // rename instead of an independent delete+add. RepositoryWatchService's own debounce window
    // already coalesces a same-window rename's Deleted+Created into back-to-back events, so this
    // window mainly bridges the rarer case where they land in different debounce flushes.
    const int RenameCorrelationWindowMilliseconds = 300;

    readonly Dictionary<string, (GitWizardRepository Repository, CancellationTokenSource ExpireCts, long SequenceNumber)> _pendingDeletedRepos = new();
    readonly Dictionary<string, long> _pendingCreateSequenceNumbers = new();
    readonly Dictionary<string, string> _reportedLiveScanErrors = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _inFlightLiveRefreshes = new(StringComparer.OrdinalIgnoreCase);
    long _liveEventSequenceNumber;

    LiveWatchController? _liveController;

    // Swappable dependencies for testability: the real Windows factory spawns an elevated broker
    // (a live UAC prompt), which an automated test can never drive. Tests substitute a fake
    // IVolumeChangeSource / elevation answer here to exercise ToggleLiveAsync end-to-end.
    Func<IVolumeChangeSource>? _liveVolumeChangeSourceFactory;

    internal Func<IVolumeChangeSource> LiveVolumeChangeSourceFactory
    {
        get => _liveVolumeChangeSourceFactory ??= CreateDefaultVolumeChangeSourceFactory();
        set => _liveVolumeChangeSourceFactory = value;
    }

    internal Func<bool> LiveIsElevated { get; set; } = VolumeChangeSourceFactory.IsElevated;

    Func<FanotifyCapabilityResult>? _liveCapabilityProbe;

    internal Func<FanotifyCapabilityResult> LiveCapabilityProbe
    {
        get => _liveCapabilityProbe ??= DefaultLiveCapabilityProbe;
        set
        {
            _liveCapabilityProbe = value;
            OnPropertyChanged(nameof(CanToggleLive));
            OnPropertyChanged(nameof(LiveTooltip));
        }
    }

    public bool IsLiveSupported
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || _liveCapabilityProbe != null)
                return LiveCapabilityProbe().IsSupported;

            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }
    }

    public string LiveTooltip
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || _liveCapabilityProbe != null)
            {
                var probe = LiveCapabilityProbe();
                if (probe.Status == FanotifyCapabilityStatus.UnsupportedKernel)
                    return "Live mode requires Linux kernel 5.9 or newer (unsupported kernel).";
                if (probe.Status == FanotifyCapabilityStatus.UnsupportedFileSystem)
                    return "Live mode is unsupported on this filesystem (fanotify marks not supported).";
            }
            else if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "Live mode currently requires Windows or Linux.";
            }

            return "Start or stop live repository monitoring (requires elevation when starting)";
        }
    }

    internal void ApplyLiveEvent(RepositoryChangeEvent ev) => _ = ApplyLiveEventAsync(ev);

    internal async Task ApplyLiveEventAsync(RepositoryChangeEvent ev)
    {
        var sequenceNumber = Interlocked.Increment(ref _liveEventSequenceNumber);
        switch (ev.Kind)
        {
            case RepositoryChangeKind.Changed:
                await ApplyLiveChangedAsync(ev.RepoRoot).ConfigureAwait(false);
                break;
            case RepositoryChangeKind.Created:
                await ApplyLiveCreatedAsync(ev.RepoRoot, sequenceNumber).ConfigureAwait(false);
                break;
            case RepositoryChangeKind.Deleted:
                ApplyLiveDeleted(ev.RepoRoot, sequenceNumber);
                break;
            case RepositoryChangeKind.Renamed:
                // RepositoryWatchService never emits this kind (see its class doc): rename
                // correlation happens here instead, from a Deleted+Created pair.
                break;
        }
    }

    async Task ApplyLiveChangedAsync(string repoRoot)
    {
        GitWizardRepository? repository = null;
        await _ui.InvokeAsync(() =>
        {
            if (!_inFlightLiveRefreshes.Add(repoRoot))
                return;

            if (_repositoryMap.TryGetValue(repoRoot, out var node))
                repository = node.Repository;
            else
                _inFlightLiveRefreshes.Remove(repoRoot);
        }).ConfigureAwait(false);

        if (repository is null)
            return;

        try
        {
            await Task.Run(() => repository.Refresh()).ConfigureAwait(false);
        }
        finally
        {
            _ui.Post(() => _inFlightLiveRefreshes.Remove(repoRoot));
        }

        _ui.Post(() => UpdateCompletedRepository(repository));
    }

    async Task ApplyLiveCreatedAsync(string repoRoot, long sequenceNumber)
    {
        await _ui.InvokeAsync(() => _pendingCreateSequenceNumbers[repoRoot] = sequenceNumber).ConfigureAwait(false);
        var repository = new GitWizardRepository(repoRoot);
        await Task.Run(() => repository.Refresh()).ConfigureAwait(false);

        _ui.Post(() =>
        {
            if (!_pendingCreateSequenceNumbers.TryGetValue(repoRoot, out var pendingSequenceNumber)
                || pendingSequenceNumber != sequenceNumber)
            {
                // Deletion invalidates the creation itself, so expiry cannot make it valid again.
                return;
            }
            _pendingCreateSequenceNumbers.Remove(repoRoot);

            if (_pendingDeletedRepos.TryGetValue(repoRoot, out var pending))
            {
                if (pending.SequenceNumber < sequenceNumber)
                {
                    _pendingDeletedRepos.Remove(repoRoot);
                    _ = CancelAndDisposeAsync(pending.ExpireCts);
                }
            }

            var renamedFrom = TryCorrelateRename(repoRoot, repository);
            if (renamedFrom != null)
                RemoveRepositoryByPath(renamedFrom);

            AddRepository(repository);
        });
    }

    void ApplyLiveDeleted(string repoRoot, long sequenceNumber)
    {
        _ui.Post(() =>
        {
            if (_pendingCreateSequenceNumbers.TryGetValue(repoRoot, out var pendingSequenceNumber)
                && pendingSequenceNumber < sequenceNumber)
                _pendingCreateSequenceNumbers.Remove(repoRoot);

            if (!_repositoryMap.TryGetValue(repoRoot, out var node))
                return;

            if (_pendingDeletedRepos.TryGetValue(repoRoot, out var existing))
            {
                _ = CancelAndDisposeAsync(existing.ExpireCts);
            }

            var expireCts = new CancellationTokenSource();
            _pendingDeletedRepos[repoRoot] = (node.Repository, expireCts, sequenceNumber);
            _ = ExpirePendingDeleteAsync(repoRoot, expireCts.Token);
        });
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

            _ = CancelAndDisposeAsync(pending.ExpireCts);
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
            {
                RemoveRepositoryByPath(repoRoot);
            }
        });
    }

    internal async Task ToggleLiveAsync()
    {
        if (IsLive || IsLiveStarting)
        {
            await StopLiveAsync().ConfigureAwait(false);
            return;
        }

        if (!IsLiveSupported)
        {
            HeaderText = $"Live watch unavailable: {LiveTooltip}";
            return;
        }

        if (IsRefreshing)
            return;

        _reportedLiveScanErrors.Clear();
        var (trackedRoots, searchRoots) = await GetLiveRootsAsync().ConfigureAwait(false);
        var controller = CreateConfiguredLiveController(trackedRoots, searchRoots);
        var detachHandlers = AttachLiveControllerHandlers(controller);
        _liveController = controller;

        await _ui.InvokeAsync(() =>
        {
            IsLiveStarting = true;
            ClearLiveScanProgress();
            HeaderText = "Starting Live watch...";
        }).ConfigureAwait(false);

        try
        {
            await RunLiveControllerAsync(controller).ConfigureAwait(false);
        }
        finally
        {
            detachHandlers();
        }
    }

    LiveWatchController CreateConfiguredLiveController(List<string> trackedRoots, List<string> searchRoots)
    {
        return new LiveWatchController(
            LiveVolumeChangeSourceFactory,
            trackedRoots,
            searchRoots,
            onEvent: ev =>
            {
                _ = ApplyLiveEventAsync(ev);
            },
            onStopped: reason => _ui.Post(() =>
            {
                IsLiveStarting = false;
                IsLive = false;
                ClearLiveScanProgress();
                JournalWarnings.Clear();
                HeaderText = $"Live watch stopped: {reason}{FormatLiveScanErrorSuffix(RequireLiveController())}";
            }),
            isElevated: LiveIsElevated);
    }

    Action AttachLiveControllerHandlers(LiveWatchController controller)
    {
        void OnStarted() => _ui.Post(() =>
        {
            if (!ReferenceEquals(_liveController, controller) || (!IsLive && !IsLiveStarting))
                return;
            IsLiveStarting = false;
            IsLive = true;
            ClearLiveScanProgress();
            HeaderText = "Live watch active";
            _reportedLiveScanErrors.Clear();
            SurfaceNewLiveScanErrors(controller);
            SurfaceNewLiveScanWarnings(controller);
        });
        void OnScanWarningsChanged() => _ui.Post(() =>
        {
            if (ReferenceEquals(_liveController, controller) && (IsLive || IsLiveStarting))
                SurfaceNewLiveScanWarnings(controller);
        });
        void OnScanErrorsChanged() => _ui.Post(() =>
        {
            if (ReferenceEquals(_liveController, controller) && (IsLive || IsLiveStarting))
                SurfaceNewLiveScanErrors(controller);
        });
        void OnLiveScanProgress(VolumeScanProgress sample) => _ui.Post(() =>
        {
            if (!ReferenceEquals(_liveController, controller) || (!IsLive && !IsLiveStarting))
                return;
            ApplyLiveScanProgress(sample);
        });

        controller.Started += OnStarted;
        controller.ScanErrorsChanged += OnScanErrorsChanged;
        controller.ScanWarningsChanged += OnScanWarningsChanged;
        controller.ScanProgress += OnLiveScanProgress;

        return () =>
        {
            controller.Started -= OnStarted;
            controller.ScanErrorsChanged -= OnScanErrorsChanged;
            controller.ScanWarningsChanged -= OnScanWarningsChanged;
            controller.ScanProgress -= OnLiveScanProgress;
        };
    }

    async Task RunLiveControllerAsync(LiveWatchController controller)
    {
        try
        {
            await controller.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            await controller.StopAsync().ConfigureAwait(false);
            GitWizardLog.LogException(exception, "Starting Live watch failed.");
            await _ui.InvokeAsync(() =>
            {
                IsLiveStarting = false;
                IsLive = false;
                ClearLiveScanProgress();
                JournalWarnings.Clear();
                HeaderText = $"Live watch could not start: {exception.Message}";
            }).ConfigureAwait(false);
        }
    }

    async Task<(List<string> Tracked, List<string> Search)> GetLiveRootsAsync()
    {
        var tracked = _allRepositories.Select(node => node.WorkingDirectory)
            .Where(path => !string.IsNullOrEmpty(path)).ToList();
        var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync().ConfigureAwait(false);
        var search = configuration.SearchPaths.Select(GitWizardApi.ExpandSearchPath).OfType<string>().ToList();
        return (tracked, search);
    }


    async Task StopLiveAsync()
    {
        if (_liveController != null)
            await _liveController.StopAsync().ConfigureAwait(false);

        await _ui.InvokeAsync(() =>
        {
            IsLiveStarting = false;
            IsLive = false;
            ClearLiveScanProgress();
            JournalWarnings.Clear();
        }).ConfigureAwait(false);
    }

    // Only onStopped requires the controller here; onEvent applies repository changes directly.
    // ToggleLiveAsync assigns _liveController before starting the controller, so onStopped can
    // rely on that assignment. This asserts the lifecycle invariant.
    LiveWatchController RequireLiveController() =>
        _liveController ?? throw new InvalidOperationException(
            "_liveController must be assigned before a Live event/stop callback can run.");


    void SurfaceNewLiveScanErrors(LiveWatchController controller)
    {
        foreach (var (drive, message) in controller.ScanErrors)
        {
            if (_reportedLiveScanErrors.TryGetValue(drive, out var previous) && previous == message)
                continue;
            _reportedLiveScanErrors[drive] = message;
            HeaderText = $"Live scan error on {drive}: {message}";
        }
        if (JournalWarnings.Count > 0)
            UpdateJournalWarningHeader();
    }

    static string FormatLiveScanErrorSuffix(LiveWatchController controller)
    {
        if (controller.ScanErrors.Count == 0)
            return string.Empty;

        var errors = string.Join("; ", controller.ScanErrors.Select(pair => $"{pair.Key}: {pair.Value}"));
        return $" (scan errors: {errors})";
    }

    Func<IVolumeChangeSource> CreateDefaultVolumeChangeSourceFactory()
    {
        return () =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var prepared = TakePreparedDiscoveryIndex();
                if (prepared != null)
                    return VolumeChangeSourceFactory.Create(prepared);
            }

            return VolumeChangeSourceFactory.Create();
        };
    }

    static FanotifyCapabilityResult DefaultLiveCapabilityProbe()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return ProbeLinuxCapability();

        return new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
    }

    [SupportedOSPlatform("linux")]
    static FanotifyCapabilityResult ProbeLinuxCapability()
    {
        try
        {
            return FanotifyInterop.Probe();
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Failed to probe Linux fanotify capabilities.");
            return new FanotifyCapabilityResult(FanotifyCapabilityStatus.UnsupportedKernel, 0);
        }
    }
}
