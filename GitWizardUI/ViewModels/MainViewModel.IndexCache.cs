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
                PreparedDiscoveryIndex.CaptureActiveCacheSessions(_liveController?.Source as IndexVolumeChangeSource)),
            () => MftIndexCacheMaintenance.Clear(GitWizardApi.GetLocalFilesPath()),
            new ReadOnlyObservableCollection<LiveVolumeStatusViewModel>(LiveVolumes),
            new ReadOnlyObservableCollection<VolumeWatchTransition>(LiveWatchEvents));
    }
}
