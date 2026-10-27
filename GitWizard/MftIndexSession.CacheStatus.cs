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

            var identities = IndexedRoots.ToDictionary(root => root.Root);
            var rows = CaptureRootStatuses().Select(drive => new MftActiveCacheRoot(
                drive, RootKeyProjection.ParseRootKeys([drive.Drive]) is [var root] &&
                    identities.TryGetValue(root, out var identity)
                    ? identity.VolumeSerial : null)).ToArray();
            return new MftActiveCacheSession(_cacheDirectory, Array.AsReadOnly(rows));
        }
    }
}
