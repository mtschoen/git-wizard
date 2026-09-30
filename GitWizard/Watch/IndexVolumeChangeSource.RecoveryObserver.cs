using MFTLib.Index;

namespace GitWizard.Watch;

// MFTLib recovers a faulted drive by itself (rescan, then restart) but raises no event when the
// restarted watch is running again; its documented consumer pattern is to wait for catch-up again
// once the drive no longer reads Recovering. The observer below does exactly that and nothing
// else: it never starts, stops or rescans a drive, it only turns the recovered drive's readiness
// back to CaughtUp. A recovery that fails reaches OnWatchFaulted as a Recovery fault instead, and
// the exclusion that follows ends the observation.
public sealed partial class IndexVolumeChangeSource
{
    internal static readonly TimeSpan RecoveryPollInterval = TimeSpan.FromMilliseconds(500);

    readonly CancellationTokenSource _observersStopping = new();
    readonly Lock _observersLock = new();
    readonly Dictionary<char, Task<bool>> _observers = [];
    readonly Dictionary<char, int> _faultGenerations = [];
    bool _observersStopped;

    /// <summary>The clock of the recovery observer's status polls; tests substitute a manual one.</summary>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    void OnDriveRecovering(char drive, WatchFault fault)
    {
        GitWizardLog.Log($"Live watch on {drive}: {fault.Kind} fault, MFTLib is rescanning the drive: " +
                         fault.Exception.Message, GitWizardLog.LogType.Verbose);
        CaptureRecoveryBaseline(drive);
        RecordDriveFault(drive);
        RaiseFaultJournalWarning(drive);
        var owner = _owner;
        PublishVolumeState(VolumeWatchStatus.CatchingUp(drive.ToString(),
            owner is null ? VolumeBlockOrigin.Unknown : CurrentOrigin(owner, drive)));
        _ = ObserveRecovery(drive);
    }

    internal static bool IsRecoveringFault(WatchFault fault) =>
        fault.Kind is WatchFaultKind.Drive or WatchFaultKind.Apply ||
        fault.Kind == WatchFaultKind.CatchUpLost &&
        fault.Exception is JournalCatchUpLostException { RecoveryStopped: false };

    // Every fault on a drive, recovering or excluding, supersedes a catch-up an observer saw
    // before it, so that observer cannot publish a stale CaughtUp over the fault's own state.
    void RecordDriveFault(char drive)
    {
        lock (_observersLock)
            _faultGenerations[drive] = _faultGenerations.GetValueOrDefault(drive) + 1;
    }

    /// <summary>
    /// The drive's one running observation, started if none runs. It completes true once the
    /// drive's recovered watch has caught up (readiness then reads CaughtUp), and false when the
    /// drive was excluded or the source is stopping. MFTLib rejects an unsettled catch-up wait
    /// inside a WatchFaulted handler, so the observation runs on the thread pool.
    /// </summary>
    Task<bool> ObserveRecovery(char drive)
    {
        var owner = _owner;
        lock (_observersLock)
        {
            if (owner is null || _observersStopped)
                return Task.FromResult(false);
            if (_observers.TryGetValue(drive, out var running))
                return running;
            var token = _observersStopping.Token;
            var observation = Task.Run(() => FollowRecoveryAsync(owner, drive, token), CancellationToken.None);
            _observers[drive] = observation;
            return observation;
        }
    }

    async Task<bool> FollowRecoveryAsync(MftIndexSession owner, char drive, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int generation;
                lock (_observersLock)
                {
                    // An excluded drive is its fault handler's to report; the observation ends here.
                    if (!ContainsUsableDrive(drive))
                    {
                        _observers.Remove(drive);
                        _recoveryBaselines.Remove(drive);
                        return false;
                    }
                    generation = _faultGenerations.GetValueOrDefault(drive);
                }

                var state = ReadWatchCatchUp(owner, drive);
                if (state is WatchCatchUpState.CatchingUp or WatchCatchUpState.CaughtUp &&
                    await TryWaitForCatchUpAsync(owner, drive, cancellationToken).ConfigureAwait(false) &&
                    TryFinishCaughtUp(owner, drive, generation, completesObservation: true))
                {
                    return true;
                }

                // Recovering, or a fault its handler has not classified yet, or a newer fault.
                await Task.Delay(RecoveryPollInterval, TimeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop or disposal ends the observation; the drive's state is theirs to report.
            return false;
        }
        catch (ObjectDisposedException)
        {
            // The index was disposed under the observation, which ends with it.
            return false;
        }
    }

    /// <summary>
    /// The one place a drive's readiness becomes CaughtUp and its observation ends. A pending
    /// recovery reconciliation is delivered first; readiness is published only if, after that
    /// work, the drive's fault generation is still <paramref name="generation" /> and MFTLib still
    /// reads it CaughtUp. Otherwise the observation stays pending: a recoverable fault is followed
    /// by the caller, and a terminal one is excluded by its own WatchFaulted notification. While a
    /// drive has a running observation only that observation completes it
    /// (<paramref name="completesObservation" />); any other caller joins it instead.
    /// </summary>
    bool TryFinishCaughtUp(MftIndexSession owner, char drive, int generation, bool completesObservation)
    {
        HashSet<string>? baseline;
        lock (_observersLock)
        {
            if (!completesObservation && _observers.ContainsKey(drive) ||
                !ReadsCaughtUpLocked(owner, drive, generation))
            {
                return false;
            }
            _recoveryBaselines.Remove(drive, out baseline);
        }

        if (baseline is not null && !DeliverRecoveryReconciliation(drive, baseline))
        {
            // Nothing was delivered, so the baseline still describes what consumers were told;
            // a newer fault's capture, taken from a block they never saw reconciled, yields to it.
            lock (_observersLock)
                _recoveryBaselines[drive] = baseline;
            return false;
        }

        // The fault handler bumps the generation under this lock, so the final check and the
        // publication cannot interleave with it. A fault that lands after the final check is
        // published by that handler afterwards, over this CaughtUp.
        lock (_observersLock)
        {
            // A newer fault's handler captured its baseline from the block, after this delivery
            // or from the same block it read, so that baseline is kept as it is.
            if (!ReadsCaughtUpLocked(owner, drive, generation))
                return false;
            if (completesObservation)
                _observers.Remove(drive);
            PublishVolumeState(VolumeWatchStatus.CaughtUp(drive.ToString(), CurrentOrigin(owner, drive)));
            return true;
        }
    }

    // MFTLib marks a faulted drive before its WatchFaulted handler bumps the generation, so the
    // library's own state is read as well as the generation.
    bool ReadsCaughtUpLocked(MftIndexSession owner, char drive, int generation) =>
        _faultGenerations.GetValueOrDefault(drive) == generation &&
        ReadWatchCatchUp(owner, drive) == WatchCatchUpState.CaughtUp;

    static WatchCatchUpState? ReadWatchCatchUp(MftIndexSession owner, char drive) =>
        owner.Index.Drives.FirstOrDefault(status => char.ToUpperInvariant(status.DriveLetter) == drive)?.WatchCatchUp;

    // False when the wait ended without the drive catching up: the watch faulted again or was
    // retired, and the loop rereads the drive's status to learn which.
    static async Task<bool> TryWaitForCatchUpAsync(MftIndexSession owner, char drive, CancellationToken cancellationToken)
    {
        try
        {
            await owner.Index.WaitForCatchUpAsync(drive, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ObjectDisposedException)
        {
            return false;
        }
    }

    async Task StopObserversAsync()
    {
        Task[] observers;
        lock (_observersLock)
        {
            _observersStopped = true;
            observers = _observers.Values.ToArray<Task>();
            _observers.Clear();
            _recoveryBaselines.Clear();
        }
        await _observersStopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(observers).ConfigureAwait(false);
    }
}
