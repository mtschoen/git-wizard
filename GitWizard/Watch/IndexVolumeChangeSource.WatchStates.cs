using MFTLib.Index;

namespace GitWizard.Watch;

public sealed partial class IndexVolumeChangeSource
{
    readonly Lock _watchStateLock = new();
    readonly Dictionary<char, long> _watchStateVersions = [];
    readonly Dictionary<char, Task> _catchUpWork = [];
    readonly Guid _watchSessionId = Guid.NewGuid();
    TaskCompletionSource _watchStateChanged = NewStateSignal();
    bool _watchStatesStopped;

    static TaskCompletionSource NewStateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    void PulseWatchStateLocked()
    {
        var previous = _watchStateChanged;
        _watchStateChanged = NewStateSignal();
        previous.TrySetResult();
    }

    internal void ApplyWatchState(DriveWatchState change)
    {
        try
        {
            AcceptWatchState(change, recordTransition: true);
        }
        catch (Exception exception)
        {
            FailWatchState(change.DriveLetter, exception);
        }
    }

    void SeedWatchStates(FileIndex index)
    {
        foreach (var status in index.Drives)
            AcceptWatchState(status.ToWatchState(), recordTransition: false);
    }

    void FailWatchState(char drive, Exception exception)
    {
        lock (_watchStateLock)
        {
            if (_watchStatesStopped)
                return;
            _output.Writer.TryComplete(exception);
        }
        RecordStartupDriveFailure(drive, exception.Message);
    }

    void AcceptWatchState(DriveWatchState change, bool recordTransition)
    {
        var drive = char.ToUpperInvariant(change.DriveLetter);
        lock (_watchStateLock)
        {
            if (_watchStatesStopped || !ContainsUsableDrive(drive) ||
                (_watchStateVersions.TryGetValue(drive, out var version) && change.StateVersion <= version))
                return;
            _watchStateVersions[drive] = change.StateVersion;
            if (recordTransition)
            {
                lock (_faultLock)
                    _readiness = _readiness.WithTransition(new VolumeWatchTransition(
                        _watchSessionId, drive.ToString(), change.StateVersion, change.CatchUpState.ToString(),
                        change.Fault?.Exception.Message, DateTimeOffset.UtcNow));
            }
            var previous = Readiness.TryGet(drive.ToString(), out var status) ? status : null;
            var origin = previous?.Origin ?? VolumeBlockOrigin.Unknown;
            // Faulted can be the first half of Faulted -> Recovering. WatchFaulted and
            // failed start results own terminal exclusion; a raw state never kills the source.
            PublishVolumeState(VolumeWatchStatus.CatchingUp(drive.ToString(), origin)
                with
            { WatchStateVersion = change.StateVersion });
            if (change.CatchUpState == WatchCatchUpState.CaughtUp)
                QueueCaughtUpLocked(drive, change.StateVersion);
            PulseWatchStateLocked();
        }
    }

}
