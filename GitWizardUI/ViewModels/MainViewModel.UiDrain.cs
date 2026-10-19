namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    // Event-driven drain: the first enqueue (or progress update) of a burst starts one batching
    // window on the injected TimeProvider; the window's expiry posts exactly one drain to the UI
    // dispatcher, which empties the command queue and applies the latest progress. Arrivals inside
    // the window ride the pending drain, preserving the coalescing described on DrainUICommands.
    // DisposeAsync stops the window and turns a drain that was already posted into a no-op.
    internal static readonly TimeSpan UiDrainBatchingWindow = TimeSpan.FromMilliseconds(250);

    internal static readonly TimeSpan UiDrainPollInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Queues a command for the next batched drain. Shutdown contract: once disposal has begun the
    /// queue is closed. Commands still pending at that moment are discarded with the view model,
    /// and a command enqueued afterwards is dropped without throwing into the producer. Nothing
    /// can be applied after disposal, so a dropped command is never observable as lost work.
    /// </summary>
    void EnqueueUICommand(RepositoryUICommand command)
    {
        if (_uiDrainDisposed)
            return;

        _uiCommands.Enqueue(command);

        // Re-check after enqueueing: disposal that began between the first check and the enqueue
        // may have cleared the queue before this command landed, so this producer clears it again.
        if (_uiDrainDisposed)
        {
            _uiCommands.Clear();
            return;
        }

        ScheduleUIDrain();
    }

    /// <summary>
    /// Waits until the command queue is empty. Returns false when disposal ended the wait, so the
    /// caller abandons the refresh instead of publishing into a disposed view model.
    /// </summary>
    internal async Task<bool> WaitForUICommandsToDrainAsync()
    {
        var shutdown = _uiDrainShutdown.Token;
        while (!_uiCommands.IsEmpty)
        {
            try
            {
                await Task.Delay(UiDrainPollInterval, _timeProvider, shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return !_uiDrainDisposed;
    }

    void ScheduleUIDrain()
    {
        if (Interlocked.CompareExchange(ref _uiDrainScheduled, 1, 0) != 0)
            return;

        lock (_uiDrainLock)
        {
            if (_uiDrainDisposed)
                return;

            _uiDrainWindow = _timeProvider.CreateTimer(OnUIDrainWindowElapsed, null,
                UiDrainBatchingWindow, Timeout.InfiniteTimeSpan);
        }
    }

    void OnUIDrainWindowElapsed(object? state)
    {
        lock (_uiDrainLock)
        {
            if (_uiDrainDisposed)
                return;

            _uiDrainWindow?.Dispose();
            _uiDrainWindow = null;
        }

        _ui.Post(RunScheduledUIDrain);
    }

    void RunScheduledUIDrain()
    {
        if (_uiDrainDisposed)
            return;

        // Reset before draining so an arrival during the drain starts the next window.
        Interlocked.Exchange(ref _uiDrainScheduled, 0);

        // Drain all pending commands in one UI dispatch to minimize layout passes
        DrainUICommands();
        ApplyProgress();
    }

    void StopUIDrain()
    {
        lock (_uiDrainLock)
        {
            _uiDrainDisposed = true;
            _uiDrainWindow?.Dispose();
            _uiDrainWindow = null;
        }

        // Ends any refresh waiting on the queue, then discards what no drain will ever apply.
        _uiDrainShutdown.Cancel();
        _uiCommands.Clear();
    }

    void ApplyProgress()
    {
        if (!_progressActive || _progressTotal <= 0)
            return;

        ProgressValue = (double)_progressCount / _progressTotal;
        ProgressText = $"{_progressDescription} {_progressCount} / {_progressTotal}";
        IsProgressVisible = true;

        if (_progressTotal == _progressCount)
        {
            IsProgressVisible = false;
        }
    }
}
