using MFTLib.Index;

namespace GitWizard.Watch;

public sealed partial class IndexVolumeChangeSource
{
    Task StopOnceAsync()
    {
        lock (_lifecycleLock)
        {
            return _stopping ??= StopCoreAsync();
        }
    }

    async Task StopWatchStateWorkAsync()
    {
        Task[] pending;
        lock (_watchStateLock)
        {
            _watchStatesStopped = true;
            PulseWatchStateLocked();
            pending = [.. _catchUpWork.Values];
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
        lock (_watchStateLock)
        {
            _catchUpWork.Clear();
            _recoveryBaselines.Clear();
        }
    }

    async Task StopCoreAsync()
    {
        // Never await a pump while holding the lock that publishes lifecycle tasks.
        await Task.Yield();
        await StopWatchStateWorkAsync().ConfigureAwait(false);
        var index = _watchedIndex;
        if (index is null)
        {
            return;
        }

        index.Changed -= OnChanged;
        index.WatchFaulted -= OnWatchFaulted;
        index.WatchStateChanged -= ApplyWatchState;
        try
        {
            // A drive's outstanding fault comes back here once more; WatchFaulted already reported it.
            var results = await index.StopWatchingAsync(index.Drives.Select(status => status.Drive), CancellationToken.None).ConfigureAwait(false);
            foreach (var result in results.Where(result => result.Outcome == DriveOperationOutcome.Failed))
            {
                GitWizardLog.Log($"Live watch on {result.Drive} stopped after a fault: " +
                                 result.Failure?.Message, GitWizardLog.LogType.Verbose);
            }
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            _disposed = true;
            _output.Writer.TryComplete();
            return new ValueTask(_disposal ??= DisposeCoreAsync(_armFinished?.Task));
        }
    }

    async Task DisposeCoreAsync(Task? armFinished)
    {
        await Task.Yield();
        // An arm following a drive's recovery must not hold disposal until that recovery ends.
        await StopWatchStateWorkAsync().ConfigureAwait(false);
        if (armFinished is not null)
        {
            await armFinished.ConfigureAwait(false);
        }

        try
        {
            await StopOnceAsync().ConfigureAwait(false);
        }
        finally
        {
            if (Session is not null)
            {
                await Session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
