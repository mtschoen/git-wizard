using MFTLib.Index;

namespace GitWizard.Watch;

public sealed partial class IndexVolumeChangeSource : IVolumeReadiness
{
    internal const string NotWatchableReason = "Live watch is not supported for this drive's index.";
    VolumeReadinessSnapshot _readiness = VolumeReadinessSnapshot.Empty;

    public WatchReadinessMode Mode => WatchReadinessMode.JournalBacklog;

    public VolumeReadinessSnapshot Readiness
    {
        get { lock (_faultLock) return _readiness; }
    }

    public event Action<VolumeReadinessSnapshot>? ReadinessChanged;

    void PublishVolumeState(VolumeWatchStatus status)
    {
        lock (_faultLock)
        {
            var snapshot = _readiness = _readiness.With(status);
            try
            {
                ReadinessChanged?.Invoke(snapshot);
            }
            catch (Exception exception)
            {
                // Readiness is observational; a subscriber fault must never fail an arm or a wait.
                GitWizardLog.LogException(exception, "A Live readiness subscriber failed.");
            }
        }
    }

    static VolumeBlockOrigin OriginOf(DriveStatus? status) => status?.BlockSource switch
    {
        BlockSource.WarmStartedFromCache => VolumeBlockOrigin.Warm,
        BlockSource.ProducedByScan => VolumeBlockOrigin.Cold,
        _ => VolumeBlockOrigin.Unknown
    };

    static VolumeBlockOrigin CurrentOrigin(MftIndexSession owner, char drive) =>
        OriginOf(owner.CaptureDriveStatuses().FirstOrDefault(status =>
            string.Equals(status.Drive, drive.ToString(), StringComparison.OrdinalIgnoreCase))?.IndexStatus);

    // Called once per arm, after ProcessArmedDrive decided _usableDrives and before the pump
    // starts, so every requested drive has a state before any catch-up marker can arrive.
    void PublishArmedDrives(MftIndexSession owner, IReadOnlyList<string> requested, Dictionary<string, string> errors)
    {
        var statuses = owner.CaptureDriveStatuses()
            .ToDictionary(status => status.Drive, StringComparer.OrdinalIgnoreCase);
        foreach (var drive in requested)
        {
            statuses.TryGetValue(drive, out var status);
            var origin = OriginOf(status?.IndexStatus);
            var letter = char.ToUpperInvariant(drive[0]);
            var usable = ContainsUsableDrive(letter);
            if (usable)
                PublishVolumeState(VolumeWatchStatus.CatchingUp(letter.ToString(), origin));
            else
                PublishVolumeState(VolumeWatchStatus.Excluded(letter.ToString(), origin,
                    errors.TryGetValue(drive, out var error) ? error : NotWatchableReason));
        }
    }

    // A block can stay queryable (DriveState.Ready) while its watch is refused or failed; that
    // drive is excluded, never relabeled healthy on the strength of DriveState alone.
    void ExcludeDrivesWithFailedWatch(MftIndexSession owner, IEnumerable<char> drives)
    {
        var driveSet = drives as ICollection<char> ?? drives.ToHashSet();
        foreach (var status in owner.CaptureDriveStatuses())
        {
            if (status.IndexStatus is not { } index)
                continue;
            var letter = char.ToUpperInvariant(status.Drive[0]);
            // A recovering drive can carry its fault's message and still come back.
            if (!driveSet.Contains(letter) || index.WatchCatchUpState == WatchCatchUpState.Recovering)
                continue;
            var message = index.WatchFailureMessage;
            if (message is { Length: > 0 } || index.WatchCatchUpState == WatchCatchUpState.Faulted)
                RecordStartupDriveFailure(letter, message ?? "The drive's watch faulted during catch-up.");
        }
    }
}
