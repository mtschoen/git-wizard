namespace GitWizard.Watch;

/// <summary>Where one watched volume stands relative to the readiness fence.</summary>
public enum VolumeWatchState
{
    CatchingUp = 0,
    CaughtUp = 1,
    Excluded = 2
}

/// <summary>Whether the volume's index block was adopted from cache or produced by a scan this run.</summary>
public enum VolumeBlockOrigin
{
    Unknown = 0,
    Warm = 1,
    Cold = 2
}

/// <summary>What a run's readiness fence means for the source that produced it.</summary>
public enum WatchReadinessMode
{
    /// <summary>The source exposes no per-volume state; readiness means the pump entered its live loop.</summary>
    PumpActive = 0,
    /// <summary>Windows: every included drive applied the USN journal backlog present at arm time.</summary>
    JournalBacklog = 1,
    /// <summary>Linux: every included root has its fanotify mark installed. No historical backlog fence exists.</summary>
    FilesystemMarks = 2
}

public sealed record VolumeWatchStatus(string Volume, VolumeWatchState State, VolumeBlockOrigin Origin, string? ExclusionReason)
{
    public static VolumeWatchStatus CatchingUp(string volume, VolumeBlockOrigin origin) => new(volume, VolumeWatchState.CatchingUp, origin, null);
    public static VolumeWatchStatus CaughtUp(string volume, VolumeBlockOrigin origin) => new(volume, VolumeWatchState.CaughtUp, origin, null);
    public static VolumeWatchStatus Excluded(string volume, VolumeBlockOrigin origin, string reason) => new(volume, VolumeWatchState.Excluded, origin, reason);
}

/// <summary>Immutable per-volume readiness. Every transition produces a new snapshot.</summary>
public sealed record VolumeReadinessSnapshot
{
    public static readonly VolumeReadinessSnapshot Empty = new([]);

    public VolumeReadinessSnapshot(IReadOnlyList<VolumeWatchStatus> volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        Volumes = Array.AsReadOnly(volumes.ToArray());
    }

    public IReadOnlyList<VolumeWatchStatus> Volumes { get; }

    /// <summary>True once every known volume is caught up or excluded. An empty snapshot is not settled.</summary>
    public bool IsSettled => Volumes.Count > 0 && Volumes.All(volume => volume.State != VolumeWatchState.CatchingUp);

    public bool HasCaughtUpVolume => Volumes.Any(volume => volume.State == VolumeWatchState.CaughtUp);

    public IReadOnlyList<string> CaughtUpVolumes =>
        Volumes.Where(volume => volume.State == VolumeWatchState.CaughtUp).Select(volume => volume.Volume).ToArray();

    public IReadOnlyList<VolumeWatchStatus> ExcludedVolumes =>
        Volumes.Where(volume => volume.State == VolumeWatchState.Excluded).ToArray();

    public bool TryGet(string volume, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VolumeWatchStatus? status)
    {
        status = Volumes.FirstOrDefault(candidate => string.Equals(candidate.Volume, volume, StringComparison.OrdinalIgnoreCase));
        return status is not null;
    }

    /// <summary>Returns a snapshot with <paramref name="status"/> replacing (or appending) its volume's entry.</summary>
    public VolumeReadinessSnapshot With(VolumeWatchStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var volumes = Volumes.Where(existing => !string.Equals(existing.Volume, status.Volume, StringComparison.OrdinalIgnoreCase))
            .Append(status)
            .OrderBy(existing => existing.Volume, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new VolumeReadinessSnapshot(volumes);
    }
}

/// <summary>
/// Optional per-volume readiness for a source. Subscribe before arming; a late subscriber reads
/// <see cref="Readiness"/> for the current state. Handlers must not block: publishers may hold
/// a lock while raising <see cref="ReadinessChanged"/>.
/// </summary>
public interface IVolumeReadiness
{
    WatchReadinessMode Mode { get; }
    VolumeReadinessSnapshot Readiness { get; }
    event Action<VolumeReadinessSnapshot>? ReadinessChanged;
}

/// <summary>The once-per-run readiness notification <see cref="RepositoryWatchService.Ready"/> raises.</summary>
public sealed record WatchReadiness(
    WatchReadinessMode Mode,
    IReadOnlyList<string> CaughtUpVolumes,
    IReadOnlyList<VolumeWatchStatus> ExcludedVolumes);
