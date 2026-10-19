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
            () => inspection.Capture(GitWizardApi.GetLocalFilesPath(), CaptureActiveCacheSessions()),
            () => MftIndexCacheMaintenance.Clear(GitWizardApi.GetLocalFilesPath()),
            new ReadOnlyObservableCollection<LiveVolumeStatusViewModel>(LiveVolumes),
            new ReadOnlyObservableCollection<VolumeWatchTransition>(LiveWatchEvents));
    }

    internal IReadOnlyList<MftActiveCacheSession> CaptureActiveCacheSessions()
    {
        var source = _liveController?.Source as IndexVolumeChangeSource;
        lock (_preparedIndexLock)
        {
            var sessions = new List<MftActiveCacheSession>();
            if (source?.CaptureCacheStatus() is { } live)
                sessions.Add(live);
            if (_preparedDiscoveryIndex?.CaptureCacheStatus() is { } prepared)
                sessions.Add(prepared);
            return sessions.AsReadOnly();
        }
    }
}
