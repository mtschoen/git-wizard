namespace GitWizard;

public sealed record MftActiveCacheRoot(MftRootStatus Drive, ulong? VolumeSerial);
public sealed record MftActiveCacheSession(
    string CacheDirectory, IReadOnlyList<MftActiveCacheRoot> Drives);
