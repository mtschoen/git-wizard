using GitWizard.Discovery;
using GitWizard.Watch;

namespace GitWizardUI.Services;

public sealed partial class LiveWatchController
{
    async Task<string?> RunOnceAsync(RepositoryWatchService service, CancellationToken ct)
    {
        string? deathReason = null;
        service.Started += OnStarted;
        service.ScanProgress += OnScanProgress;
        service.Stopped += OnStopped;
        service.ScanErrorsChanged += OnScanErrorsChanged;
        service.ScanWarningsChanged += OnScanWarningsChanged;
        service.InventoryReady += OnInventoryReady;
        service.Ready += OnReady;
        service.VolumeReadinessChanged += OnVolumeReadinessChanged;

        try
        {
            await foreach (var changeEvent in service.RunAsync(ct).ConfigureAwait(false))
            {
                onEvent(changeEvent);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on caller-initiated cancellation (StopAsync or the caller's own token).
        }
        finally
        {
            service.Started -= OnStarted;
            service.ScanProgress -= OnScanProgress;
            service.Stopped -= OnStopped;
            service.ScanErrorsChanged -= OnScanErrorsChanged;
            service.ScanWarningsChanged -= OnScanWarningsChanged;
            service.InventoryReady -= OnInventoryReady;
            service.Ready -= OnReady;
            service.VolumeReadinessChanged -= OnVolumeReadinessChanged;
        }

        return deathReason;

        void OnStopped(string reason) => deathReason = reason;
        void OnReady(WatchReadiness readiness) => Ready?.Invoke(readiness);
        void OnVolumeReadinessChanged(VolumeReadinessSnapshot snapshot) => VolumeReadinessChanged?.Invoke(snapshot);
    }

    void OnStarted() => Started?.Invoke();
    void OnScanProgress(VolumeScanProgress sample) => ScanProgress?.Invoke(sample);
    void OnScanErrorsChanged() => ScanErrorsChanged?.Invoke();
    void OnScanWarningsChanged() => ScanWarningsChanged?.Invoke();
    void OnInventoryReady(DiscoveryInventory inventory) => InventoryReady?.Invoke(inventory);
}
