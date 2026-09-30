using MFTLib.Index;

namespace GitWizard.Watch;

public sealed partial class IndexVolumeChangeSource
{
    readonly Lock _startupBufferLock = new();
    List<VolumeChangeBatch>? _startupBuffer;
    bool _startupBuffering;
    readonly Dictionary<char, string> _startupErrors = [];

    internal MftIndexSession? Session => _owner;
    internal FileIndex? Index => _owner?.Index;

    public static IndexVolumeChangeSource CreatePrepared(MftIndexSession owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new IndexVolumeChangeSource(
            (_, _, _) => throw new InvalidOperationException("Strict prepared source cannot open a new session."),
            owner);
    }

    public void StartStartupBuffering()
    {
        lock (_startupBufferLock)
        {
            _startupBuffer = [];
            _startupBuffering = true;
        }
    }

    public IReadOnlyList<VolumeChangeBatch> CompleteStartupBuffer()
    {
        lock (_startupBufferLock)
        {
            _startupBuffering = false;
            var batches = _startupBuffer ?? [];
            _startupBuffer = null;
            return batches;
        }
    }

    /// <summary>Awaited after a drive's startup catch-up wait succeeds, before the arm reports it.</summary>
    internal Func<char, Task>? StartupCatchUpSettledForTest { get; init; }

    async Task WaitDriveAsync(MftIndexSession owner, char drive, CancellationToken token)
    {
        var upper = char.ToUpperInvariant(drive);
        var generation = CurrentFaultGeneration(upper);
        try
        {
            await owner.Index.WaitForCatchUpAsync(upper, token).ConfigureAwait(false);
            if (StartupCatchUpSettledForTest is { } settled)
                await settled(upper).ConfigureAwait(false);
            // A fault after this catch-up settled supersedes it; its recovery is followed below.
            if (TryFinishCaughtUp(owner, upper, generation, completesObservation: false))
                return;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The wait faults before MFTLib marks a recovering drive, so the fault is not classified
            // here: the WatchFaulted handler keeps or excludes the drive, and the observation follows
            // a kept drive through its recovery. ExcludeDrivesWithFailedWatch records an exclusion.
            GitWizardLog.Log($"Live watch on {char.ToUpperInvariant(drive)}: catch-up wait ended without catching up: " +
                             exception.Message, GitWizardLog.LogType.Verbose);
        }
        await ObserveRecovery(upper).WaitAsync(token).ConfigureAwait(false);
    }

    void RecordStartupDriveFailure(char drive, string message)
    {
        var upper = char.ToUpperInvariant(drive);
        VolumeBlockOrigin origin;
        RecordDriveFault(upper);
        RemoveUsableDrive(upper);
        lock (_faultLock)
        {
            _startupErrors[upper] = message;
            origin = _readiness.TryGet(upper.ToString(), out var previous) ? previous.Origin : VolumeBlockOrigin.Unknown;
        }
        PublishVolumeState(VolumeWatchStatus.Excluded(upper.ToString(), origin, message));
        DriveFailed?.Invoke(upper.ToString(), message);
    }
}
