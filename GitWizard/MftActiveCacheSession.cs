namespace GitWizard;

public sealed record MftActiveCacheDrive(MftDriveStatus Drive, uint? VolumeSerial);
public sealed record MftActiveCacheSession(
    string CacheDirectory, IReadOnlyList<MftActiveCacheDrive> Drives);
