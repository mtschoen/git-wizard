using System.Collections.ObjectModel;
using GitWizard;
using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    public IndexCacheStatusViewModel CreateIndexCacheStatusViewModel()
    {
        var inspection = new MftIndexCacheInspection();
        return new IndexCacheStatusViewModel(_ui, () => inspection.Capture(
            GitWizardApi.GetLocalFilesPath(), CaptureActiveCacheSessions()),
            new ReadOnlyObservableCollection<LiveVolumeStatusViewModel>(LiveVolumes));
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
