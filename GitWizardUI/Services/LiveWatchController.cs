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
    readonly bool _includeTempDirectories;
    readonly IReadOnlyCollection<string> _ignoredPaths;

    public event Action? Started;
    public event Action? ScanErrorsChanged;
    public event Action? ScanWarningsChanged;
    public event Action<VolumeScanProgress>? ScanProgress;

    // Guards the read-modify-write of the mutable run state below. StartAsync's loop and a
    // concurrent StopAsync both touch _stopRequested/_runCts/_source; without this a StopAsync
    // landing between iterations could no-op its cancel while the loop respawned a fresh source.
    readonly Lock _gate = new();
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
        : this(sourceFactory, trackedRoots, searchRoots, onEvent, onStopped, new LiveWatchOptions { IsElevated = isElevated })
    {
    }

    public LiveWatchController(
        Func<IVolumeChangeSource> sourceFactory,
        IReadOnlyCollection<string> trackedRoots,
        IReadOnlyCollection<string> searchRoots,
        Action<RepositoryChangeEvent> onEvent,
        Action<string> onStopped,
        LiveWatchOptions? options = null)
    {
        _sourceFactory = sourceFactory;
        _trackedRoots = trackedRoots;
        _searchRoots = searchRoots;
        _onEvent = onEvent;
        _onStopped = onStopped;
        _isElevated = options?.IsElevated ?? (() => false);
        _includeTempDirectories = options?.IncludeTempDirectories ?? false;
        _ignoredPaths = options?.IgnoredPaths ?? [];
    }

    public IReadOnlyDictionary<string, string> ScanErrors
    {
        get
        {
            lock (_gate)
                return _service?.ScanErrors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyCollection<JournalWarning> ScanWarnings
    {
        get
        {
            lock (_gate)
                return _service?.ScanWarnings ?? Array.Empty<JournalWarning>();
        }
    }

    public IVolumeChangeSource? Source
    {
        get
        {
            lock (_gate)
                return _source;
        }
    }

    const int MaxConsecutiveRespawns = 3;
    const int RespawnBackoffMilliseconds = 250;

    public async Task StartAsync(CancellationToken ct)
    {
        var consecutiveRespawns = 0;
        while (true)
        {
            RepositoryWatchService service;
            CancellationTokenSource runCts;
            lock (_gate)
            {
                // Atomic top-of-iteration re-check: a StopAsync that raced the previous
                // iteration's teardown wins here, before a new source is ever created.
                if (_stopRequested || ct.IsCancellationRequested)
                    return;

                var source = _sourceFactory();
                service = new RepositoryWatchService(source, _trackedRoots, _searchRoots,
                    includeTempDirectories: _includeTempDirectories,
                    ignoredPaths: _ignoredPaths);
                runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _source = source;
                _service = service;
                _runCts = runCts;
            }

            string? died;
            try
            {
                died = await RunOnceAsync(service, runCts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _runCts = null;
                    runCts.Dispose();
                }
            }

            bool stopRequested;
            lock (_gate)
                stopRequested = _stopRequested;

            var elevatedRespawn =
                !stopRequested && !ct.IsCancellationRequested && died is not null && _isElevated()
                && consecutiveRespawns < MaxConsecutiveRespawns;

            // Whoever ran this source owns disposing it (no-op if a concurrent StopAsync already
            // claimed it), so both the respawn and the terminal paths leave no leaked handle.
            await DisposeCurrentSourceAsync().ConfigureAwait(false);

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
                _onStopped(died);
            return;
        }
    }

    public async Task StopAsync()
    {
        IVolumeChangeSource? source;
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
            source = _source;
            _source = null;
        }

        // Force-dispose outside the lock: a source whose WatchAsync ignores its token only
        // unwinds when its handle is disposed, so this is what actually stops the run in that case.
        if (source is not null)
            await source.DisposeAsync().ConfigureAwait(false);
    }

    async Task DisposeCurrentSourceAsync()
    {
        IVolumeChangeSource? source;
        lock (_gate)
        {
            source = _source;
            _source = null;
        }

        if (source is not null)
            await source.DisposeAsync().ConfigureAwait(false);
    }

    async Task<string?> RunOnceAsync(RepositoryWatchService service, CancellationToken ct)
    {
        string? deathReason = null;
        service.Started += OnStarted;
        service.ScanProgress += OnScanProgress;
        void OnStopped(string reason) => deathReason = reason;
        service.Stopped += OnStopped;
        service.ScanErrorsChanged += OnScanErrorsChanged;
        service.ScanWarningsChanged += OnScanWarningsChanged;

        try
        {
            await foreach (var changeEvent in service.RunAsync(ct).ConfigureAwait(false))
                _onEvent(changeEvent);
        }
        catch (OperationCanceledException)
        {
            // Expected on caller-initiated cancellation (StopAsync or the caller's own token).
        }
        finally
        {
            service.Started -= OnStarted;
            service.ScanProgress -= OnScanProgress;
            service.Stopped -= OnStopped;
            service.ScanErrorsChanged -= OnScanErrorsChanged;
            service.ScanWarningsChanged -= OnScanWarningsChanged;
        }

        return deathReason;
    }

    void OnStarted() => Started?.Invoke();
    void OnScanProgress(VolumeScanProgress sample) => ScanProgress?.Invoke(sample);
    void OnScanErrorsChanged() => ScanErrorsChanged?.Invoke();
    void OnScanWarningsChanged() => ScanWarningsChanged?.Invoke();
}

/// <summary>
/// Optional configuration settings for <see cref="LiveWatchController"/>.
/// </summary>
public sealed class LiveWatchOptions
{
    /// <summary>
    /// Probe function indicating whether the process currently has elevated privileges.
    /// </summary>
    public Func<bool>? IsElevated { get; init; }

    /// <summary>
    /// When true, includes temp directories in live change tracking and discovery.
    /// </summary>
    public bool IncludeTempDirectories { get; init; }

    /// <summary>
    /// Configured ignored paths to exclude from live change tracking and discovery.
    /// </summary>
    public IReadOnlyCollection<string>? IgnoredPaths { get; init; }
}
