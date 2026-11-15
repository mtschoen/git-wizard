using MFTLib.Index;

namespace GitWizard.Watch;

// Fault handling split out of IndexVolumeChangeSource.cs to keep that file under the size limit.
public sealed partial class IndexVolumeChangeSource
{
    // A drive MFTLib is recovering (Drive, Apply, or a lost catch-up it is still retrying) stays
    // usable and is reported quietly; a drive whose block could not store a change stays usable and
    // is reported with the rescan that repairs it; every other fault ends that drive's watch until
    // the user restarts it, and the source dies once no usable drive is left.
    void OnWatchFaulted(WatchFault fault)
    {
        if (fault.Kind == WatchFaultKind.Subscriber)
        {
            // A handler error reaches the reader as an error, never as a broker death.
            _output.Writer.TryComplete(fault.Exception);
            return;
        }
        var drive = fault.Volume.Letter;
        if (fault.Kind == WatchFaultKind.CapacityExhausted)
        {
            // Not a watch failure: the watch keeps running and later changes still arrive.
            try
            {
                GitWizardLog.Log($"Live watch on {drive}: a journal change could not be stored in the index block " +
                                 $"({fault.Exception.Message}). Watching continues; rescan {drive}: to restore a " +
                                 "complete index.", GitWizardLog.LogType.Warning);
                RaiseCapacityWarning(drive, fault.Exception.Message);
                RaiseFaultJournalWarning(drive);
            }
            catch (Exception exception)
            {
                // MFTLib discards fault-reporter exceptions, so route them to our reader explicitly.
                _output.Writer.TryComplete(exception);
            }
            return;
        }
        if (fault.IsRecovering)
        {
            try
            {
                CaptureRecoveryBaseline(drive);
                GitWizardLog.Log($"Live watch on {drive}: {fault.Kind} fault, MFTLib is rescanning the drive: " +
                                 fault.Exception.Message, GitWizardLog.LogType.Verbose);
                RaiseFaultJournalWarning(drive);
            }
            catch (Exception exception)
            {
                // A failed baseline cannot be reconciled, even if the library's rescan succeeds.
                FailWatchState(drive, exception);
            }
            return;
        }
        bool terminal;
        var owner = Session;
        var origin = owner is null ? VolumeBlockOrigin.Unknown : CurrentOrigin(owner, drive);
        lock (_watchStateLock)
        {
            terminal = !RemoveUsableDrive(drive);
            _recoveryBaselines.Remove(drive);
            long? observedVersion = _watchStateVersions.TryGetValue(drive, out var value) ? value : null;
            PublishVolumeState(VolumeWatchStatus.Excluded(drive.ToString(), origin, fault.Exception.Message)
                with
            { WatchStateVersion = observedVersion });
            PulseWatchStateLocked();
        }
        try
        {
            RaiseFaultJournalWarning(drive);
            DriveFailed?.Invoke(drive.ToString(), fault.Exception.Message);
        }
        catch (Exception exception)
        {
            // MFTLib discards fault-reporter exceptions, so route them to our reader explicitly.
            _output.Writer.TryComplete(exception);
        }
        if (terminal)
        {
            ReportDeath(fault.Exception.Message);
        }
    }

    void ReportDeath(string reason)
    {
        if (Interlocked.Exchange(ref _deathReported, 1) != 0)
        {
            return;
        }

        try
        {
            SourceDied?.Invoke(reason);
        }
        catch (Exception exception)
        {
            _output.Writer.TryComplete(exception);
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }
}
