using System.Collections.ObjectModel;
using GitWizardUI.Services;

namespace GitWizardUI.ViewModels;

// Capacity warnings: drives whose index block could not store a journal change. They travel with
// the journal hints (the same snapshot event and the same clear points) and offer one action, the
// rescan a Live start performs.
public sealed partial class LiveWatchViewModel
{
    public ObservableCollection<CapacityWarningViewModel> CapacityWarnings { get; } = [];

    // Rows are replaced only when the drives' warnings changed, so an unrelated warning update
    // leaves the list (and any row status) as it is.
    void SurfaceCapacityWarnings(LiveWatchController controller)
    {
        var current = controller.CapacityWarnings
            .OrderBy(warning => warning.DriveLetter, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (current.SequenceEqual(CapacityWarnings.Select(row => row.Warning)))
        {
            return;
        }

        CapacityWarnings.Clear();
        foreach (var warning in current)
        {
            CapacityWarnings.Add(new CapacityWarningViewModel(warning));
        }
    }

    /// <summary>
    /// The capacity warning's rescan action. Every Live start runs the watch preparation, which
    /// rescans each drive whose block is flagged before the watch arms
    /// (<c>RepositoryDiscoveryCoordinator.NeedsScan</c>), so the action restarts Live. Returns null
    /// once the restart has begun, or the reason it was refused.
    /// </summary>
    public async Task<string?> RestartLiveToRescanAsync()
    {
        if (!IsLiveSupported)
        {
            return $"Live watch unavailable: {LiveTooltip}";
        }

        if (_refresh.IsRefreshing)
        {
            return "Wait for the refresh to finish, then rescan.";
        }

        if (IsLive || IsLiveStarting)
        {
            await StopLiveAsync().ConfigureAwait(false);
        }

        // The started run lasts until Live stops, so the action does not wait for it.
        _ = ToggleLiveAsync().RunAndLogFaults("Restarting Live watch to rescan");
        return null;
    }
}
