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
        try
        {
            await owner.Index.WaitForCatchUpAsync(upper, token).ConfigureAwait(false);
            if (StartupCatchUpSettledForTest is { } settled)
                await settled(upper).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Recovery state and loss information are recorded before the library faults this wait.
            GitWizardLog.Log($"Live watch on {upper}: catch-up wait ended without catching up: " +
                exception.Message, GitWizardLog.LogType.Verbose);
        }
        await WaitForPublishedDriveAsync(upper, token).ConfigureAwait(false);
    }

    async Task WaitForPublishedDriveAsync(char drive, CancellationToken token)
    {
        while (true)
        {
            Task changed;
            lock (_watchStateLock)
            {
                if (_watchStatesStopped || !ContainsUsableDrive(drive))
                    return;
                if (Readiness.TryGet(drive.ToString(), out var status) &&
                    status.State == VolumeWatchState.CaughtUp)
                    return;
                changed = _watchStateChanged.Task;
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    void RecordStartupDriveFailure(char drive, string message)
    {
        var upper = char.ToUpperInvariant(drive);
        lock (_watchStateLock)
        {
            if (_watchStatesStopped)
                return;
            RemoveUsableDrive(upper);
            _recoveryBaselines.Remove(upper);
            VolumeBlockOrigin origin;
            lock (_faultLock)
            {
                _startupErrors[upper] = message;
                origin = _readiness.TryGet(upper.ToString(), out var previous) ? previous.Origin : VolumeBlockOrigin.Unknown;
            }
            long? observedVersion = _watchStateVersions.TryGetValue(upper, out var value) ? value : null;
            PublishVolumeState(VolumeWatchStatus.Excluded(upper.ToString(), origin, message)
                with
            { WatchStateVersion = observedVersion });
            PulseWatchStateLocked();
        }
        DriveFailed?.Invoke(upper.ToString(), message);
    }
}
