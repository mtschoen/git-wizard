using GitWizard;
using GitWizard.Watch;

namespace GitWizardUI.Services;

/// <summary>
/// Owns one <see cref="RepositoryWatchService"/> run at a time, forwarding its events and
/// deciding whether to respawn silently or surface a stop after the underlying source dies.
/// Framework-free by design (no Avalonia dependency) so it is directly unit-testable; callers
/// that need UI-thread marshaling must do it themselves inside <c>onEvent</c>/<c>onStopped</c> -
/// this type never touches a dispatcher.
/// </summary>
/// <remarks>
/// The primary construction takes a startup factory producing a coordinator-prepared
/// <see cref="WatchStartupResult"/> (an already-armed source plus its reconciled inventory);
/// every start and respawn re-runs the factory, so membership is reconciled against the exact
/// session being watched each time. A startup with no watchable source is terminal - it is
/// reported through <see cref="StartupUnavailable"/> without a respawn or a second elevation
/// attempt. The source-factory construction remains for fake-source tests and arms the source
/// inside the service, the pre-coordinator shape.
/// </remarks>
public sealed class LiveWatchController
{
    readonly Func<IVolumeChangeSource>? _sourceFactory;
    readonly Func<IProgress<WatchStartupProgress>, CancellationToken, Task<WatchStartupResult>>? _startupFactory;
    readonly IReadOnlyCollection<string> _trackedRoots;
    readonly IReadOnlyCollection<string> _searchRoots;
    readonly Action<RepositoryChangeEvent> _onEvent;
    readonly Action<string> _onStopped;
    readonly Func<bool> _isElevated;
    readonly bool _includeTempDirectories;
    readonly IReadOnlyCollection<string> _ignoredPaths;

    public event Action? Started;
    public event Action? ScanErrorsChanged;

    internal int ScanErrorsSubscriberCount => ScanErrorsChanged?.GetInvocationList().Length ?? 0;
    public event Action? ScanWarningsChanged;
    public event Action<VolumeScanProgress>? ScanProgress;
    public event Action<WatchReadiness>? Ready;
    public event Action<VolumeReadinessSnapshot>? VolumeReadinessChanged;
    public event Action<WatchStartupProgress>? StartupProgress;

    /// <summary>Forwarded from the running service: the reconciled startup inventory, before <see cref="Started"/>.</summary>
    public event Action<DiscoveryInventory>? InventoryReady;

    /// <summary>
    /// Raised once, terminally, when the coordinator's startup has no watchable source (an
    /// unusable or cleanly empty discovery). No respawn follows; the consumer surfaces the
    /// inventory's outcome and errors.
    /// </summary>
    public event Action<DiscoveryInventory>? StartupUnavailable;

    // Guards the read-modify-write of the mutable run state below. StartAsync's loop and a
    // concurrent StopAsync both touch _stopRequested/_runCts/_lifetime; without this a StopAsync
    // landing between iterations could no-op its cancel while the loop respawned a fresh source.
    readonly Lock _gate = new();
    IAsyncDisposable? _lifetime;
    RepositoryWatchService? _service;
    CancellationTokenSource? _runCts;
    bool _stopRequested;

    internal RepositoryWatchService? Service
    {
        get { lock (_gate) return _service; }
    }

    public WatchReadiness? Readiness
    {
        get
        {
            lock (_gate)
                return _service?.Readiness;
        }
    }

    public LiveWatchController(
        Func<IProgress<WatchStartupProgress>, CancellationToken, Task<WatchStartupResult>> startupFactory,
        Action<RepositoryChangeEvent> onEvent,
        Action<string> onStopped,
        LiveWatchOptions? options = null)
    {
        _startupFactory = startupFactory ?? throw new ArgumentNullException(nameof(startupFactory));
        _trackedRoots = [];
        _searchRoots = [];
        _onEvent = onEvent;
        _onStopped = onStopped;
        _isElevated = options?.IsElevated ?? (() => false);
        _includeTempDirectories = options?.IncludeTempDirectories ?? false;
        _ignoredPaths = options?.IgnoredPaths ?? [];
    }

    public LiveWatchController(
        Func<CancellationToken, Task<WatchStartupResult>> startupFactory,
        Action<RepositoryChangeEvent> onEvent,
        Action<string> onStopped,
        LiveWatchOptions? options = null)
        : this((_, ct) => (startupFactory ?? throw new ArgumentNullException(nameof(startupFactory)))(ct), onEvent, onStopped, options)
    {
    }

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
                return _lifetime switch
                {
                    IVolumeChangeSource source => source,
                    WatchStartupResult startup => startup.Source,
                    _ => null
                };
        }
    }

    const int MaxConsecutiveRespawns = 3;
    const int RespawnBackoffMilliseconds = 250;

    public async Task StartAsync(CancellationToken ct)
    {
        var consecutiveRespawns = 0;
        while (true)
        {
            // Published before startup acquisition so StopAsync's cancel lands while the
            // coordinator prepare (or a fake's blocking arm) is still in flight - a stop
            // during acquisition must unwind the loop, not wait out the prepare.
            CancellationTokenSource runCts;
            lock (_gate)
            {
                if (_stopRequested || ct.IsCancellationRequested)
                    return;
                runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _runCts = runCts;
            }

            try
            {
                var (startup, startRun) = await AcquireStartupAsync(runCts, ct).ConfigureAwait(false);
                if (!startRun)
                    return;

                var service = TryBeginRun(startup);
                if (service is null)
                {
                    // A stop raced the startup acquisition; abandon the prepared startup.
                    if (startup is not null)
                        await startup.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                string? died = await RunOnceAsync(service, runCts.Token).ConfigureAwait(false);

                bool stopRequested;
                lock (_gate)
                    stopRequested = _stopRequested;

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
                    _onStopped(died);
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

    // Acquires the coordinator-prepared startup for this run (null in source-factory mode).
    // StartRun is false when the loop must end: a cancelled acquisition, a terminal
    // null-source startup (reported through StartupUnavailable with no respawn and no second
    // elevation attempt), or a stop that raced the acquisition.
    async Task<(WatchStartupResult? Startup, bool StartRun)> AcquireStartupAsync(
        CancellationTokenSource runCts, CancellationToken ct)
    {
        if (_startupFactory is null)
            return (null, true);

        WatchStartupResult startup;
        try
        {
            var progress = new SynchronousProgress<WatchStartupProgress>(report => StartupProgress?.Invoke(report));
            startup = await _startupFactory(progress, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            return (null, false);
        }

        if (startup.Source is not null)
            return (startup, true);

        var inventory = startup.Inventory;
        await startup.DisposeAsync().ConfigureAwait(false);
        if (IsStopRequested(ct))
            return (null, false);
        StartupUnavailable?.Invoke(inventory);
        return (null, false);
    }

    bool IsStopRequested(CancellationToken ct)
    {
        lock (_gate)
            return _stopRequested || ct.IsCancellationRequested;
    }

    // Publishes the next run's service and lifetime under the gate after the atomic
    // top-of-run stop re-check; returns null when a stop won the race, leaving the
    // caller to abandon the startup it acquired.
    RepositoryWatchService? TryBeginRun(WatchStartupResult? startup)
    {
        lock (_gate)
        {
            if (_stopRequested)
                return null;

            RepositoryWatchService service;
            if (startup is not null)
            {
                service = new RepositoryWatchService(startup);
                _lifetime = startup;
            }
            else
            {
                var source = (_sourceFactory ?? throw new InvalidOperationException(
                    "A source factory is required when no startup factory is configured."))();
                service = new RepositoryWatchService(source, _trackedRoots, _searchRoots,
                    includeTempDirectories: _includeTempDirectories,
                    ignoredPaths: _ignoredPaths);
                _lifetime = source;
            }

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
            await lifetime.DisposeAsync().ConfigureAwait(false);
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
            await lifetime.DisposeAsync().ConfigureAwait(false);
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
        service.InventoryReady += OnInventoryReady;
        void OnReady(WatchReadiness readiness) => Ready?.Invoke(readiness);
        void OnVolumeReadinessChanged(VolumeReadinessSnapshot snapshot) => VolumeReadinessChanged?.Invoke(snapshot);
        service.Ready += OnReady;
        service.VolumeReadinessChanged += OnVolumeReadinessChanged;

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
            service.InventoryReady -= OnInventoryReady;
            service.Ready -= OnReady;
            service.VolumeReadinessChanged -= OnVolumeReadinessChanged;
        }

        return deathReason;
    }

    void OnStarted() => Started?.Invoke();
    void OnScanProgress(VolumeScanProgress sample) => ScanProgress?.Invoke(sample);
    void OnScanErrorsChanged() => ScanErrorsChanged?.Invoke();
    void OnScanWarningsChanged() => ScanWarningsChanged?.Invoke();
    void OnInventoryReady(DiscoveryInventory inventory) => InventoryReady?.Invoke(inventory);
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
