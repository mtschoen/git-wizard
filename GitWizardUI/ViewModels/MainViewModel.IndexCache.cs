using System.Collections.ObjectModel;
using GitWizard;
using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    IndexCacheViewModel? _indexCache;

    public IndexCacheViewModel IndexCache => _indexCache ??= CreateIndexCache();

    IndexCacheViewModel CreateIndexCache()
    {
        var inspection = new MftIndexCacheInspection();
        return new IndexCacheViewModel(_ui,
            () => inspection.Capture(GitWizardApi.GetLocalFilesPath(),
                PreparedDiscoveryIndex.CaptureActiveCacheSessions(LiveWatch.Controller?.Source as IndexVolumeChangeSource)),
            () => MftIndexCacheMaintenance.Clear(GitWizardApi.GetLocalFilesPath()),
            new ReadOnlyObservableCollection<LiveVolumeStatusViewModel>(LiveWatch.LiveVolumes),
            new ReadOnlyObservableCollection<VolumeWatchTransition>(LiveWatch.LiveWatchEvents));
    }

    public async Task ClearCacheAsync()
    {
        await PreparedDiscoveryIndex.SetAsync(null).ConfigureAwait(false);
        GitWizardApi.ClearCache();
        await _dialogs.ShowAlertAsync("Cache Cleared", "Repository cache has been cleared");
    }

    public async Task DeleteAllLocalFilesAsync()
    {
        await PreparedDiscoveryIndex.SetAsync(null).ConfigureAwait(false);
        if (!GitWizardApi.TryDeleteAllLocalFiles(out var refusal))
        {
            await _dialogs.ShowAlertAsync("Files Not Deleted", refusal);
            return;
        }
        await _dialogs.ShowAlertAsync("Files Deleted", "All local files have been deleted");
    }
}
