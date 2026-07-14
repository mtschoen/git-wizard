namespace GitWizard.Watch;

/// <summary>One privileged handle streaming filesystem change batches for a set of volumes.</summary>
public interface IVolumeChangeSource : IAsyncDisposable
{
    event Action<string>? SourceDied;

    Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct);

    IAsyncEnumerable<VolumeChangeBatch> WatchAsync(CancellationToken ct);
}
