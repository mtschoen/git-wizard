using GitWizard.Watch;

namespace GitWizardUI.Services;

/// <summary>
/// Owns one <see cref="RepositoryWatchService"/> run at a time, forwarding its events and
/// deciding whether to respawn silently or surface a stop after the underlying source dies.
/// Framework-free by design (no Avalonia dependency) so it is directly unit-testable; callers
/// that need UI-thread marshaling must do it themselves inside <c>onEvent</c>/<c>onStopped</c> -
/// this type never touches a dispatcher.
/// </summary>
public sealed class LiveWatchController
{
    readonly Func<IVolumeChangeSource> _sourceFactory;
    readonly IReadOnlyCollection<string> _trackedRoots;
    readonly IReadOnlyCollection<string> _searchRoots;
    readonly Action<RepositoryChangeEvent> _onEvent;
    readonly Action<string> _onStopped;
    readonly Func<bool> _isElevated;

    IVolumeChangeSource? _source;
    RepositoryWatchService? _service;
    CancellationTokenSource? _runCts;
    bool _stopRequested;

    public LiveWatchController(
        Func<IVolumeChangeSource> sourceFactory,
        IReadOnlyCollection<string> trackedRoots,
        IReadOnlyCollection<string> searchRoots,
        Action<RepositoryChangeEvent> onEvent,
        Action<string> onStopped,
        Func<bool> isElevated)
    {
        _sourceFactory = sourceFactory;
        _trackedRoots = trackedRoots;
        _searchRoots = searchRoots;
        _onEvent = onEvent;
        _onStopped = onStopped;
        _isElevated = isElevated;
    }

    public IReadOnlyDictionary<string, string> ScanErrors =>
        _service?.ScanErrors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public async Task StartAsync(CancellationToken ct)
    {
        _stopRequested = false;
        while (!ct.IsCancellationRequested)
        {
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runCts = runCts;
            var died = await RunOnceAsync(runCts.Token).ConfigureAwait(false);
            _runCts = null;

            if (_stopRequested || ct.IsCancellationRequested)
                return;

            if (died is null)
                return;

            if (!_isElevated())
            {
                _onStopped(died);
                return;
            }
            // Elevated: loop again and respawn silently via a fresh sourceFactory() call.
        }
    }

    public async Task StopAsync()
    {
        _stopRequested = true;
        _runCts?.Cancel();
        if (_source is { } source)
            await source.DisposeAsync().ConfigureAwait(false);
    }

    // Runs one source's worth of the watch service to completion, returning the death reason
    // (null if the run ended because of caller-initiated cancellation/StopAsync rather than the
    // source dying).
    async Task<string?> RunOnceAsync(CancellationToken ct)
    {
        var source = _sourceFactory();
        _source = source;
        var service = new RepositoryWatchService(source, _trackedRoots, _searchRoots);
        _service = service;

        string? deathReason = null;
        service.Stopped += reason => deathReason = reason;

        try
        {
            await foreach (var changeEvent in service.RunAsync(ct).ConfigureAwait(false))
                _onEvent(changeEvent);
        }
        catch (OperationCanceledException)
        {
            // Expected on caller-initiated cancellation.
        }

        return deathReason;
    }
}
