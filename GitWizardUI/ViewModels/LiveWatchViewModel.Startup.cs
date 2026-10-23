using GitWizard;
using GitWizard.Watch;
using GitWizardUI.Services;

namespace GitWizardUI.ViewModels;

// Live-mode startup lifecycle: every start and respawn runs the discovery coordinator's
// watch preparation (arm first, then reconcile membership against the watched session),
// applies the reconciled inventory to the visible repositories, and surfaces terminal
// startup outcomes instead of starting the pump.
public sealed partial class LiveWatchViewModel
{
    public async Task ToggleLiveAsync()
    {
        if (IsLive || IsLiveStarting)
        {
            await StopLiveAsync().ConfigureAwait(false);
            return;
        }

        if (!IsLiveSupported)
        {
            _statusHeader.ShowLiveMessage($"Live watch unavailable: {LiveTooltip}");
            return;
        }

        if (_refresh.IsRefreshing)
        {
            return;
        }

        _reportedLiveScanErrors.Clear();
        var trackedRoots = _repositoryList.AllRepositories.Select(node => node.WorkingDirectory)
            .Where(path => !string.IsNullOrEmpty(path)).ToList();
        var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync().ConfigureAwait(false);
        var controller = CreateConfiguredLiveController(configuration, trackedRoots);
        var detachHandlers = AttachLiveControllerHandlers(controller);
        Controller = controller;

        await _ui.InvokeAsync(() =>
        {
            IsLiveStarting = true;
            ClearLiveScanProgress();
            _statusHeader.ShowLiveMessage("Starting Live watch...");
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

    // Every Live start and respawn goes through the discovery coordinator with the currently
    // tracked repositories as retained exact roots, so the watch arms the same session that
    // reconciles membership (including repositories created while Live was closed and cached
    // roots outside the configured search roots' drives).
    LiveWatchController CreateConfiguredLiveController(
        GitWizardConfiguration configuration,
        IReadOnlyList<string> trackedRoots) =>
        new(
            (progress, cancellationToken) => PrepareLiveWatchAsync(configuration, trackedRoots, progress, cancellationToken),
            onEvent: ev => _ = ApplyLiveEventAsync(ev),
            onStopped: reason => _ui.Post(() =>
            {
                IsLiveStarting = false;
                IsLive = false;
                ClearLiveScanProgress();
                ClearLiveReadiness();
                var controller = RequireLiveController();
                // A death stop keeps the drives' journal hints on screen: they explain why the
                // watch ended, including the sole-drive case where the terminal header is all
                // the UI shows. A user-initiated stop still clears them (StopLiveAsync).
                SurfaceNewLiveScanWarnings(controller);
                _statusHeader.ShowLiveMessage($"Live watch stopped: {reason}{FormatLiveScanErrorSuffix(controller)}");
            }),
            IsElevated);

    // The prepared discovery owner is always transferred to watch preparation, which decides
    // per platform whether to adopt or dispose it. Withholding it here on a platform check
    // left the view model holding an owner the first start should have consumed.
    Task<WatchStartupResult> PrepareLiveWatchAsync(
        GitWizardConfiguration configuration,
        IReadOnlyList<string> trackedRoots,
        IProgress<WatchStartupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var prepared = _preparedDiscoveryIndex.Take();
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false, trackedRoots);
        return PrepareWatchAsync(request, prepared, progress, cancellationToken);
    }

    // Applies the coordinator's reconciled startup inventory to the visible membership before
    // Live events flow: repositories the inventory claims (including ones created while Live
    // was closed) are added, and paths it no longer claims are removed. A partial inventory
    // only adjusts observed scope - ReconcilePaths retains entries discovery did not observe.
    internal async Task ApplyLiveStartupInventoryAsync(DiscoveryInventory inventory)
    {
        var membership = RepositoryDiscoveryCoordinator.ReconcilePaths(
            _repositoryList.AllRepositories.Select(node => node.WorkingDirectory).ToArray(), inventory);
        var membershipSet = new HashSet<string>(membership, StringComparer.OrdinalIgnoreCase);

        List<string> addedPaths = [];
        await _ui.InvokeAsync(() =>
        {
            var current = _repositoryList.AllRepositories.Select(node => node.WorkingDirectory).ToArray();
            foreach (var path in current)
            {
                if (!membershipSet.Contains(path))
                {
                    _repositoryList.RemoveRepositoryPathFromUi(path);
                }
            }

            var currentSet = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
            addedPaths = membership.Where(path => !currentSet.Contains(path)).ToList();
        }).ConfigureAwait(false);

        foreach (var path in addedPaths)
        {
            var repository = new GitWizardRepository(path);
            await Task.Run(() => repository.Refresh()).ConfigureAwait(false);
            // A short-lived repository may already be gone by the time its refresh ran.
            if (!Directory.Exists(path))
            {
                continue;
            }

            _ui.Post(() => _repositoryList.AddRepository(repository, streaming: _refresh.IsRefreshing));
        }
    }

    Action AttachLiveControllerHandlers(LiveWatchController controller)
    {
        void PostIfActive(Action action) => _ui.Post(() =>
        {
            if (ReferenceEquals(Controller, controller) && (IsLive || IsLiveStarting))
            {
                action();
            }
        });

        void OnStarted() => PostIfActive(() =>
        {
            IsLiveStarting = false;
            IsLive = true;
            ClearLiveScanProgress();
            _statusHeader.ShowLiveMessage("Live watch active");
            _reportedLiveScanErrors.Clear();
            SurfaceNewLiveScanErrors(controller);
            SurfaceNewLiveScanWarnings(controller);
        });
        void OnScanWarningsChanged() => PostIfActive(() => SurfaceNewLiveScanWarnings(controller));
        void OnScanErrorsChanged() => PostIfActive(() => SurfaceNewLiveScanErrors(controller));
        void OnLiveScanProgress(VolumeScanProgress sample) => PostIfActive(() => ApplyLiveScanProgress(sample));
        void OnReady(WatchReadiness readiness) => PostIfActive(() => ApplyLiveReady(readiness));
        void OnVolumeReadinessChanged(VolumeReadinessSnapshot snapshot) => PostIfActive(() => ApplyLiveVolumeReadiness(snapshot));
        void OnStartupProgress(WatchStartupProgress progress) => PostIfActive(() => ApplyLiveStartupProgress(progress));
        void OnInventoryReady(DiscoveryInventory inventory) => _ = ApplyLiveStartupInventoryAsync(inventory);
        void OnStartupUnavailable(DiscoveryInventory inventory) => PostIfActive(() =>
        {
            IsLiveStarting = false;
            IsLive = false;
            ClearLiveScanProgress();
            ClearLiveReadiness();
            JournalWarnings.Clear();
            if (inventory.Outcome == DiscoveryOutcome.Unusable)
            {
                foreach (var (root, message) in inventory.Errors)
                {
                    _errorLog.PostError(_ui, $"Live discovery error on {root}: {message}");
                }

                _statusHeader.ShowLiveMessage($"Live watch could not start: {inventory.Errors.Values.FirstOrDefault() ?? "discovery unusable"}");
            }
            else
            {
                _statusHeader.ShowLiveMessage("No tracked repositories found; nothing to watch.");
            }
        });

        controller.Started += OnStarted;
        controller.ScanErrorsChanged += OnScanErrorsChanged;
        controller.ScanWarningsChanged += OnScanWarningsChanged;
        controller.ScanProgress += OnLiveScanProgress;
        controller.Ready += OnReady;
        controller.VolumeReadinessChanged += OnVolumeReadinessChanged;
        controller.StartupProgress += OnStartupProgress;
        controller.InventoryReady += OnInventoryReady;
        controller.StartupUnavailable += OnStartupUnavailable;

        return () =>
        {
            controller.Started -= OnStarted;
            controller.ScanErrorsChanged -= OnScanErrorsChanged;
            controller.ScanWarningsChanged -= OnScanWarningsChanged;
            controller.ScanProgress -= OnLiveScanProgress;
            controller.Ready -= OnReady;
            controller.VolumeReadinessChanged -= OnVolumeReadinessChanged;
            controller.StartupProgress -= OnStartupProgress;
            controller.InventoryReady -= OnInventoryReady;
            controller.StartupUnavailable -= OnStartupUnavailable;
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
                ClearLiveReadiness();
                JournalWarnings.Clear();
                _statusHeader.ShowLiveMessage($"Live watch could not start: {exception.Message}");
            }).ConfigureAwait(false);
        }
    }

    async Task StopLiveAsync()
    {
        if (Controller != null)
        {
            await Controller.StopAsync().ConfigureAwait(false);
        }

        await _ui.InvokeAsync(() =>
        {
            IsLiveStarting = false;
            IsLive = false;
            ClearLiveScanProgress();
            ClearLiveReadiness();
            JournalWarnings.Clear();
        }).ConfigureAwait(false);
    }

    LiveWatchController RequireLiveController() =>
        Controller ?? throw new InvalidOperationException(
            "Controller must be assigned before a Live event/stop callback can run.");

    void SurfaceNewLiveScanErrors(LiveWatchController controller)
    {
        foreach (var (drive, message) in controller.ScanErrors)
        {
            if (!_reportedLiveScanErrors.TryReport(drive, message))
            {
                continue;
            }

            _statusHeader.ShowLiveMessage($"Live scan error on {drive}: {message}");
        }
        if (JournalWarnings.Count > 0)
        {
            UpdateJournalWarningHeader();
        }
    }

    static string FormatLiveScanErrorSuffix(LiveWatchController controller) =>
        controller.ScanErrors.Count == 0
            ? string.Empty
            : $" (scan errors: {string.Join("; ", controller.ScanErrors.Select(pair => $"{pair.Key}: {pair.Value}"))})";
}
