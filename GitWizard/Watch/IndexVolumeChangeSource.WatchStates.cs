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

    internal void ApplyWatchState(WatchStateChange change)
    {
        try
        {
            AcceptWatchState(change.DriveLetter, change.Watch, change.Fault, recordTransition: true);
        }
        catch (Exception exception)
        {
            FailWatchState(change.DriveLetter, exception);
        }
    }

    void SeedWatchStates(FileIndex index)
    {
        foreach (var status in index.Drives)
            AcceptWatchState(status.DriveLetter, status.Watch, null, recordTransition: false);
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

    void AcceptWatchState(char driveLetter, DriveWatchStatus watch, WatchFault? fault, bool recordTransition)
    {
        var drive = char.ToUpperInvariant(driveLetter);
        lock (_watchStateLock)
        {
            if (_watchStatesStopped || !ContainsUsableDrive(drive) ||
                (_watchStateVersions.TryGetValue(drive, out var version) && watch.StateVersion <= version))
                return;
            _watchStateVersions[drive] = watch.StateVersion;
            if (recordTransition)
            {
                lock (_faultLock)
                    _readiness = _readiness.WithTransition(new VolumeWatchTransition(
                        _watchSessionId, drive.ToString(), watch.StateVersion, watch.State.ToString(),
                        fault?.Exception.Message, DateTimeOffset.UtcNow));
            }
            var previous = Readiness.TryGet(drive.ToString(), out var status) ? status : null;
            var origin = previous?.Origin ?? VolumeBlockOrigin.Unknown;
            // Faulted can be the first half of Faulted -> Recovering. WatchFaulted and
            // failed start results own terminal exclusion; a raw state never kills the source.
            PublishVolumeState(VolumeWatchStatus.CatchingUp(drive.ToString(), origin)
                with
            { WatchStateVersion = watch.StateVersion });
            if (watch.State == WatchState.CaughtUp)
                QueueCaughtUpLocked(drive, watch.StateVersion);
            PulseWatchStateLocked();
        }
    }

}
