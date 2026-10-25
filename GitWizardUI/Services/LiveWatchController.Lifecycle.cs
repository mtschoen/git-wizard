using GitWizard.Watch;

namespace GitWizardUI.Services;

public sealed partial class LiveWatchController
{
    const int MaxConsecutiveRespawns = 3;
    const int RespawnBackoffMilliseconds = 250;

    public async Task StartAsync(CancellationToken ct)
    {
        var consecutiveRespawns = 0;
        while (true)
        {
            var runCts = TryCreateRunCancellation(ct);
            if (runCts is null)
            {
                return;
            }

            try
            {
                var startup = await AcquireStartupAsync(runCts, ct).ConfigureAwait(false);
                if (startup is null)
                {
                    return;
                }

                var service = TryBeginRun(startup);
                if (service is null)
                {
                    // A stop raced the startup acquisition; abandon the prepared startup.
                    await startup.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                var died = await RunOnceAsync(service, runCts.Token).ConfigureAwait(false);

                bool stopRequested;
                lock (_gate)
                {
                    stopRequested = _stopRequested;
                }

                var elevatedRespawn =
                    !stopRequested && !ct.IsCancellationRequested && died is not null && _isElevated()
                    && consecutiveRespawns < MaxConsecutiveRespawns;

                // Whoever ran this source owns disposing it (no-op if a concurrent StopAsync already
                // claimed it), so both the respawn and the terminal paths leave no leaked handle.
                await DisposeCurrentLifetimeAsync().ConfigureAwait(false);

                if (elevatedRespawn)
                {
                    consecutiveRespawns++;
                    try
                    {
                        await Task.Delay(RespawnBackoffMilliseconds, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                if (!stopRequested && !ct.IsCancellationRequested && died is not null)
                {
                    onStopped(died);
                }

                return;
            }
            finally
            {
                lock (_gate)
                {
                    _runCts = null;
                    runCts.Dispose();
                }
            }
        }
    }

    // Publish before startup acquisition so StopAsync can cancel an in-flight prepare.
    // Admission and publication share the disposal gate, preserving the stop/respawn handoff.
    CancellationTokenSource? TryCreateRunCancellation(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_stopRequested || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runCts = runCancellation;
            return runCancellation;
        }
    }

    // Acquires the coordinator-prepared startup for this run.
    // Returns null when the loop must end: a cancelled acquisition, a terminal
    // null-source startup (reported through StartupUnavailable with no respawn and no second
    // elevation attempt), or a stop that raced the acquisition.
    async Task<WatchStartupResult?> AcquireStartupAsync(
        CancellationTokenSource runCts, CancellationToken ct)
    {
        WatchStartupResult startup;
        try
        {
            var progress = new SynchronousProgress<WatchStartupProgress>(report => StartupProgress?.Invoke(report));
            startup = await _startupFactory(progress, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            return null;
        }

        if (startup.Source is not null)
        {
            return startup;
        }

        var inventory = startup.Inventory;
        await startup.DisposeAsync().ConfigureAwait(false);
        if (IsStopRequested(ct))
        {
            return null;
        }

        StartupUnavailable?.Invoke(inventory);
        return null;
    }

    bool IsStopRequested(CancellationToken ct)
    {
        lock (_gate)
        {
            return _stopRequested || ct.IsCancellationRequested;
        }
    }

    // Publishes the next run's service and lifetime under the gate after the atomic
    // top-of-run stop re-check; returns null when a stop won the race, leaving the
    // caller to abandon the startup it acquired.
    RepositoryWatchService? TryBeginRun(WatchStartupResult startup)
    {
        lock (_gate)
        {
            if (_stopRequested)
            {
                return null;
            }

            var service = new RepositoryWatchService(startup);
            _lifetime = startup;
            _service = service;
            return service;
        }
    }

    public async Task StopAsync()
    {
        IAsyncDisposable? lifetime;
        lock (_gate)
        {
            _stopRequested = true;
            // CA1849 would have this await CancelAsync, which cannot be done while holding
            // the gate. Moving the cancel outside it is worse, not better: the run loop
            // disposes this same source in its finally under this very gate, so releasing
            // the gate first lets the loop finish and dispose it before the cancel lands,
            // and CancelAsync then throws ObjectDisposedException. Cancelling here, under
            // the gate that also guards disposal, is what makes the handoff safe.
#pragma warning disable CA1849
            _runCts?.Cancel();
#pragma warning restore CA1849
            lifetime = _lifetime;
            _lifetime = null;
        }

        // Force-dispose outside the lock: a source whose WatchAsync ignores its token only
        // unwinds when its handle is disposed, so this is what actually stops the run in that case.
        if (lifetime is not null)
        {
            await lifetime.DisposeAsync().ConfigureAwait(false);
        }
    }

    async Task DisposeCurrentLifetimeAsync()
    {
        IAsyncDisposable? lifetime;
        lock (_gate)
        {
            lifetime = _lifetime;
            _lifetime = null;
        }

        if (lifetime is not null)
        {
            await lifetime.DisposeAsync().ConfigureAwait(false);
        }
    }
}
