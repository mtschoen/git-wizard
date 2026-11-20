using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GitWizard.Watch;

namespace GitWizardTests.TestSupport;

/// <summary>
/// Scripted source with per-volume readiness. Every fence is driven from the test: arm result,
/// per-volume state, batches, drive failure, source death, channel fault.
/// </summary>
sealed class ReadinessVolumeChangeSource : IVolumeChangeSource, IVolumeChangeDiagnostics, IVolumeReadiness, IPumpLifecycle
{
    readonly Channel<VolumeChangeBatch> _batches = Channel.CreateUnbounded<VolumeChangeBatch>();
    readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _pumpStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Lock _gate = new();
    VolumeReadinessSnapshot _readiness = VolumeReadinessSnapshot.Empty;

    public event Action<string>? SourceDied;
    public event Action<string, string>? DriveFailed;
    public event Action<JournalWarning>? JournalWarningDetected;
    public event Action? CapacityWarningsChanged { add { } remove { } }
    public event Action<VolumeReadinessSnapshot>? ReadinessChanged;

    /// <summary>
    /// True (the default) reproduces every caller that does not care about the pump-started
    /// race: the pump is reported active before any test even starts the run, matching the old
    /// unconditional fence. Pass false to hold PumpStarted pending until the test calls
    /// <see cref="SignalPumpStarted"/>, for tests that exercise the fence itself.
    /// </summary>
    public ReadinessVolumeChangeSource(bool autoStartPump = true)
    {
        if (autoStartPump)
        {
            _pumpStarted.TrySetResult();
        }
    }

    public WatchReadinessMode Mode { get; init; } = WatchReadinessMode.JournalBacklog;
    public Task Started => _started.Task;
    public Task PumpStarted => _pumpStarted.Task;
    public int ReadinessSubscribers => ReadinessChanged?.GetInvocationList().Length ?? 0;

    public void SignalPumpStarted() => _pumpStarted.TrySetResult();

    public VolumeReadinessSnapshot Readiness
    {
        get
        {
            lock (_gate)
            {
                return _readiness;
            }
        }
    }

    /// <summary>Per-volume failures every arm reports.</summary>
    public IReadOnlyDictionary<string, string>? ArmErrors { get; init; }
    public IReadOnlyCollection<string>? ArmedVolumes { get; private set; }
    /// <summary>Awaited by <see cref="DisposeAsync"/> after it counts the call, to hold cleanup open.</summary>
    public Func<ValueTask>? DisposeGate { get; init; }
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    int _disposeCount;

    public Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        ArmedVolumes = [.. volumes];
        return Task.FromResult(new VolumeArmResult(
            new Dictionary<string, string>(ArmErrors ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase), []));
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        _started.TrySetResult();
        await foreach (var batch in _batches.Reader.ReadAllAsync(ct))
        {
            yield return batch;
        }
    }

    public void SetVolume(VolumeWatchStatus status)
    {
        VolumeReadinessSnapshot snapshot;
        lock (_gate)
        {
            snapshot = _readiness = _readiness.With(status);
        }

        ReadinessChanged?.Invoke(snapshot);
    }

    public void SetSnapshot(VolumeReadinessSnapshot snapshot)
    {
        lock (_gate)
        {
            _readiness = snapshot;
        }

        ReadinessChanged?.Invoke(snapshot);
    }

    /// <summary>Raises death and then a settling transition inside one call, the tightest race the contract must survive.</summary>
    public void KillThenSettle(string reason, VolumeWatchStatus status)
    {
        SourceDied?.Invoke(reason);
        SetVolume(status);
    }

    public void FailDrive(string drive, string message) => DriveFailed?.Invoke(drive, message);
    public void RaiseJournalWarning(JournalWarning warning) => JournalWarningDetected?.Invoke(warning);
    public void KillSource(string reason) => SourceDied?.Invoke(reason);
    public void Publish(VolumeChangeBatch batch) => Assert.That(_batches.Writer.TryWrite(batch), Is.True);
    public void Complete() => _batches.Writer.TryComplete();
    public void Fail(Exception exception) => _batches.Writer.TryComplete(exception);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        Complete();
        return DisposeGate?.Invoke() ?? ValueTask.CompletedTask;
    }
}
