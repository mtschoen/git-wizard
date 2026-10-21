using GitWizard.Watch;

namespace GitWizardTests;

// Arms only when ReleaseArm is called, so a test can observe the amber mid-arm state, and
// reports IVolumeScanProgress samples on demand. With a non-null armFailure the release throws
// it from ArmAndCatchUpAsync, taking the same InvalidOperationException path as a real open
// failure (MainViewModel.RunLiveControllerAsync's catch).
internal sealed class GatedProgressVolumeChangeSource : IVolumeChangeSource, IVolumeScanProgress
{
    readonly TaskCompletionSource _armStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _armReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Exception? _armFailure;

    public GatedProgressVolumeChangeSource(Exception? armFailure = null) => _armFailure = armFailure;

    event Action<string>? IVolumeChangeSource.SourceDied
    {
        add { }
        remove { }
    }

    public event Action<VolumeScanProgress>? ScanProgress;

    public Task ArmStarted => _armStarted.Task;

    public async Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        _armStarted.TrySetResult();
        await _armReleased.Task.WaitAsync(ct);
        if (_armFailure is not null)
            throw _armFailure;
        return new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public void ReportScanProgress(VolumeScanProgress sample) => ScanProgress?.Invoke(sample);
    public void ReleaseArm() => _armReleased.TrySetResult();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
