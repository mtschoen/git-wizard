namespace GitWizard.Watch;

public sealed partial class RepositoryWatchService
{
    // One lock serializes the three fences and the terminal flag against the single Ready
    // publication. Ready is invoked while this lock is held so that a death or cancellation
    // observed a moment later cannot interleave a late marker after Stopped; handlers must not
    // block (the CLI writes a line, the controller posts to a dispatcher).
    readonly Lock _readinessLock = new();
    VolumeReadinessSnapshot _volumeReadiness = VolumeReadinessSnapshot.Empty;
    WatchReadinessMode _readinessMode = WatchReadinessMode.PumpActive;
    IReadOnlyList<string> _pumpVolumes = [];
    bool _startupDelivered;
    bool _terminal;
    WatchReadiness? _readiness;

    /// <summary>
    /// Raised exactly once per <see cref="RunAsync"/> when every requested volume is caught up or
    /// explicitly excluded, at least one is caught up, B's startup batches have been delivered and
    /// the pump is entering its live loop. Never raised after <see cref="Stopped"/>, after the
    /// consumer's cancellation, or after a pump fault. Later per-volume changes flow through
    /// <see cref="VolumeReadinessChanged"/> without a second notification.
    /// </summary>
    public event Action<WatchReadiness>? Ready;

    internal int ReadySubscriberCount => Ready?.GetInvocationList().Length ?? 0;

    /// <summary>Per-volume state forwarded from the source, starting with the snapshot read when the run subscribed.</summary>
    public event Action<VolumeReadinessSnapshot>? VolumeReadinessChanged;

    public WatchReadiness? Readiness
    {
        get
        {
            lock (_readinessLock)
            {
                return _readiness;
            }
        }
    }

    void ResetReadiness(WatchReadinessMode mode)
    {
        lock (_readinessLock)
        {
            _volumeReadiness = VolumeReadinessSnapshot.Empty;
            _readinessMode = mode;
            _pumpVolumes = [];
            _startupDelivered = false;
            _terminal = false;
            _readiness = null;
        }
    }

    void OnVolumeReadinessChanged(VolumeReadinessSnapshot snapshot)
    {
        lock (_readinessLock)
        {
            _volumeReadiness = snapshot;
            VolumeReadinessChanged?.Invoke(snapshot);
            TryPublishReadyLocked();
        }
    }

    void SetPumpVolumes(IReadOnlyList<string> volumes)
    {
        lock (_readinessLock)
        {
            _pumpVolumes = volumes;
        }
    }

    // Called by the pump after startup batches flushed and the source read is established,
    // before the live loop begins.
    void MarkStartupDelivered()
    {
        lock (_readinessLock)
        {
            _startupDelivered = true;
            TryPublishReadyLocked();
        }
    }

    void MarkTerminal()
    {
        lock (_readinessLock)
        {
            _terminal = true;
        }
    }

    void TryPublishReadyLocked()
    {
        if (_readiness is not null || _terminal || !_startupDelivered)
        {
            return;
        }

        WatchReadiness readiness;
        if (_readinessMode == WatchReadinessMode.PumpActive)
        {
            readiness = new WatchReadiness(WatchReadinessMode.PumpActive, _pumpVolumes, []);
        }
        else
        {
            if (!_volumeReadiness.IsSettled || !_volumeReadiness.HasCaughtUpVolume)
            {
                return;
            }

            readiness = new WatchReadiness(_readinessMode, _volumeReadiness.CaughtUpVolumes, _volumeReadiness.ExcludedVolumes);
        }

        _readiness = readiness;
        Ready?.Invoke(readiness);
    }
}
