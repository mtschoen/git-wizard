namespace GitWizard;

public sealed class NonRepositoryProbeEntry
{
    public DateTimeOffset LastWriteTimeUtc { get; set; }
    public DateTimeOffset LastUsedUtc { get; set; }
}
