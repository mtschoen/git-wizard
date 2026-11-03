namespace GitWizard;

public sealed class NonRepositoryProbeEntry
{
    public const int CurrentPolicyVersion = 2;

    public DateTimeOffset LastWriteTimeUtc { get; set; }
    public DateTimeOffset LastUsedUtc { get; set; }
    public int PolicyVersion { get; set; }
}
