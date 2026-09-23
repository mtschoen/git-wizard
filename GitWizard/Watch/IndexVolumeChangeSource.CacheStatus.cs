namespace GitWizard.Watch;

public sealed partial class IndexVolumeChangeSource
{
    public MftActiveCacheSession? CaptureCacheStatus()
    {
        MftIndexSession? owner;
        lock (_lifecycleLock)
        {
            if (_disposed)
                return null;
            owner = _owner;
        }
        return owner?.CaptureCacheStatus();
    }
}
