namespace GitWizard;

public sealed record MftActiveCacheRoot(MftRootStatus Drive, uint? VolumeSerial);
public sealed record MftActiveCacheSession(
    string CacheDirectory, IReadOnlyList<MftActiveCacheRoot> Drives);
