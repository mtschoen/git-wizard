using GitWizard.Watch;

namespace GitWizardTests.TestSupport;

sealed partial class ScriptedVolumeChangeSource(
    IReadOnlyList<VolumeChangeBatch>? batches = null,
    IReadOnlyDictionary<string, string>? errors = null) : IVolumeChangeSource, IVolumeScanProgress
{
    readonly IReadOnlyList<VolumeChangeBatch> _batches = batches ?? [];
    readonly IReadOnlyDictionary<string, string> _errors =
        errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    readonly TaskCompletionSource _armStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _watchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Lock _gate = new();

    Action<string>? _sourceDied;
    string? _deathReason;
    int _disposeCount;

    public bool KeepWatching { get; init; }
    public Task? ArmGate { get; init; }
    public Exception? ArmFailure { get; init; }
    public bool ReplayDeathToLateSubscribers { get; init; }

    public Task ArmStarted => _armStarted.Task;
    public Task Armed => _armed.Task;
    public Task WatchStarted => _watchStarted.Task;
    public bool IsArmed => _armed.Task.IsCompletedSuccessfully;
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public event Action<string>? SourceDied
    {
        add
        {
            lock (_gate)
            {
                _sourceDied += value;
                if (ReplayDeathToLateSubscribers && _deathReason is not null)
                {
                    value?.Invoke(_deathReason);
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _sourceDied -= value;
            }
        }
    }

    public event Action<VolumeScanProgress>? ScanProgress;

    public async Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        _armStarted.TrySetResult();
        if (ArmGate is not null)
        {
            await ArmGate.WaitAsync(ct).ConfigureAwait(false);
        }

        if (ArmFailure is not null)
        {
            throw ArmFailure;
        }

        _armed.TrySetResult();
        return new VolumeArmResult(_errors, []);
    }
}
