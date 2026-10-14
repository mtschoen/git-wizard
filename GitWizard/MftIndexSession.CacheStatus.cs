namespace GitWizard;

public sealed partial class MftIndexSession
{
    public MftActiveCacheSession? CaptureCacheStatus()
    {
        lock (_disposalLock)
        {
            if (IsDisposed || _cacheDirectory is null)
                return null;
            var identities = IndexedDrives.ToDictionary(
                drive => drive.Drive.Letter);
            var rows = CaptureDriveStatuses().Select(drive => new MftActiveCacheDrive(
                drive, drive.Drive.Length > 0 && identities.TryGetValue(
                    char.ToUpperInvariant(drive.Drive[0]), out var identity)
                    ? identity.VolumeSerial : null)).ToArray();
            return new MftActiveCacheSession(_cacheDirectory, Array.AsReadOnly(rows));
        }
    }
}
