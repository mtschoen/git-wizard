namespace GitWizard.Watch;

public sealed partial class IndexVolumeChangeSource
{
    /// <summary>Observes queued readiness work so tests can assert the absence of publication after it settles.</summary>
    internal Task WaitForCatchUpWorkForTestAsync()
    {
        lock (_watchStateLock)
            return Task.WhenAll(_catchUpWork.Values);
    }

    void QueueCaughtUpLocked(char drive, long version)
    {
        var previous = _catchUpWork.GetValueOrDefault(drive, Task.CompletedTask);
        _catchUpWork[drive] = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                CompleteCaughtUp(drive, version);
            }
            catch (Exception exception)
            {
                FailWatchState(drive, exception);
            }
        }, CancellationToken.None);
    }

    bool IsCurrentWatchStateLocked(char drive, long version) =>
        !_watchStatesStopped && ContainsUsableDrive(drive) &&
        _watchStateVersions.TryGetValue(drive, out var current) && current == version;

    void CompleteCaughtUp(char drive, long version)
    {
        HashSet<string>? baseline;
        var owner = _owner;
        if (owner is null)
            return;
        lock (_watchStateLock)
        {
            if (!IsCurrentWatchStateLocked(drive, version))
                return;
            _recoveryBaselines.Remove(drive, out baseline);
        }
        if (baseline is not null && !DeliverRecoveryReconciliation(drive, baseline))
        {
            // Overwrites on purpose: while the delivery ran, a newer fault may have captured a
            // baseline from the recovered block this delivery never reported. That one is
            // missing the changes since the baseline this call removed, which the retried
            // reconciliation must still deliver, so the removed baseline wins.
            lock (_watchStateLock)
            {
                if (!_watchStatesStopped)
                    _recoveryBaselines[drive] = baseline;
            }
            return;
        }
        var origin = CurrentOrigin(owner, drive);
        lock (_watchStateLock)
        {
            if (!IsCurrentWatchStateLocked(drive, version))
                return;
            PublishVolumeState(VolumeWatchStatus.CaughtUp(drive.ToString(), origin)
                with
            { WatchStateVersion = version });
            PulseWatchStateLocked();
        }
    }
}
