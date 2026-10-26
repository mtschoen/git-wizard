using System.Runtime.CompilerServices;
using GitWizard.Watch;

namespace GitWizardTests.TestSupport;

sealed partial class ScriptedVolumeChangeSource
{
    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        _watchStarted.TrySetResult();
        foreach (var batch in _batches)
        {
            ct.ThrowIfCancellationRequested();
            yield return batch;
            await Task.Yield();
        }
        if (KeepWatching)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
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
        Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }
}
