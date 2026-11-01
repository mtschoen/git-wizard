namespace GitWizard;

public sealed partial class MftIndexSession
{
    public MftActiveCacheSession? CaptureCacheStatus()
    {
        lock (_disposalLock)
        {
            if (IsDisposed || _cacheDirectory is null)
            {
                return null;
            }

            var rows = CaptureRootStatuses().Select(drive => new MftActiveCacheRoot(
                drive, drive.IndexStatus?.VolumeSerial)).ToArray();
            return new MftActiveCacheSession(_cacheDirectory, Array.AsReadOnly(rows));
        }
    }

}
