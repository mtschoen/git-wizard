using GitWizard.Watch;

namespace GitWizardTests.UI;

// A source whose WatchAsync hangs until cancelled or killed, and that records whether it was
// armed/disposed - used to deterministically observe LiveWatchController's respawn/stop wiring
// without racing a scripted batch against a kill signal.
internal sealed class HangingKillableSource : IVolumeChangeSource, IVolumeScanProgress
{
    readonly Lock _gate = new();
    Action<string>? _sourceDied;
    string? _deathReason;

    public event Action<string>? SourceDied
    {
        add
        {
            lock (_gate)
            {
                _sourceDied += value;
                if (_deathReason is not null)
                    value?.Invoke(_deathReason);
            }
        }
        remove
        {
            lock (_gate)
                _sourceDied -= value;
        }
    }

    public event Action<VolumeScanProgress>? ScanProgress;

    readonly TaskCompletionSource _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Armed => _armed.Task;
    public bool IsArmed => _armed.Task.IsCompletedSuccessfully;
    public int DisposeCount { get; private set; }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        _armed.TrySetResult();
        return Task.FromResult(new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public void KillSource(string reason)
    {
        lock (_gate)
        {
            _deathReason = reason;
            _sourceDied?.Invoke(reason);
        }
    }

    public void ReportScanProgress(VolumeScanProgress sample) => ScanProgress?.Invoke(sample);

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
