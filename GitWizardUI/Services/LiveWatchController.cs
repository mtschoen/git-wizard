using GitWizard.Discovery;
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
/// attempt./// </remarks>
public sealed partial class LiveWatchController(
    Func<IProgress<WatchStartupProgress>, CancellationToken, Task<WatchStartupResult>> startupFactory,
    Action<RepositoryChangeEvent> onEvent,
    Action<string> onStopped,
    Func<bool> isElevated)
{
    readonly Func<IProgress<WatchStartupProgress>, CancellationToken, Task<WatchStartupResult>> _startupFactory = startupFactory ?? throw new ArgumentNullException(nameof(startupFactory));
    readonly Func<bool> _isElevated = isElevated ?? throw new ArgumentNullException(nameof(isElevated));

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
}
