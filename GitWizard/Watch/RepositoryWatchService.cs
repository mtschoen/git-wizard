using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using JetBrains.Annotations;

namespace GitWizard.Watch;

/// <summary>
/// Watches a set of volumes for filesystem changes via an <see cref="IVolumeChangeSource"/>
/// and maps them to <see cref="RepositoryChangeEvent"/>s for the tracked/search repository
/// roots. Repeated changes to the same root within one debounce window coalesce into a
/// single <see cref="RepositoryChangeKind.Changed"/> event. A repository whose .git directory
/// creation classifies under a search root (or is probed from a MovedIn candidate) is adopted
/// into that volume's running filter, so its later edits classify as
/// <see cref="RepositoryChangeKind.Changed"/> without a watch restart. Rename correlation
/// (matching a <see cref="RepositoryChangeKind.Deleted"/> against a
/// <see cref="RepositoryChangeKind.Created"/>) is deliberately left to the consumer - this
/// service never inspects git remotes. Directory renames arrive as Deleted + MovedIn: an
/// untracked MovedIn path under a search root is probed once at flush time for a .git entry
/// (directory or gitfile) and surfaces as Created on a hit.
/// </summary>
public sealed partial class RepositoryWatchService
{
    readonly IVolumeChangeSource _source;
    readonly IReadOnlyCollection<string> _trackedRoots;
    readonly IReadOnlyCollection<string> _searchRoots;
    readonly IReadOnlyCollection<string> _ignoredPaths;
    readonly TimeSpan _debounce;
    readonly bool _includeTempDirectories;

    readonly Lock _errorsLock = new();
    FrozenDictionary<string, string> _scanErrors = FrozenDictionary<string, string>.Empty;
    FrozenSet<JournalWarning> _scanWarnings = [];

    public event Action? ScanErrorsChanged;

    internal int ScanErrorsSubscriberCount => ScanErrorsChanged?.GetInvocationList().Length ?? 0;
    public event Action? ScanWarningsChanged;
    public event Action? Started;
    public event Action<string>? Stopped;
    public event Action<VolumeScanProgress>? ScanProgress;

    /// <summary>
    /// Immutable snapshot of initial scan failures and subsequent watch failures, keyed by drive.
    /// A failed watch does not imply that the drive's index is unavailable for queries.
    /// </summary>
    [UsedImplicitly]
    public IReadOnlyDictionary<string, string> ScanErrors { get { lock (_errorsLock) { return _scanErrors; } } }

    [UsedImplicitly]
    public IReadOnlyCollection<JournalWarning> ScanWarnings { get { lock (_errorsLock) { return _scanWarnings; } } }

    public RepositoryWatchService(
        IVolumeChangeSource source,
        IReadOnlyCollection<string> trackedRoots,
        IReadOnlyCollection<string> searchRoots,
        TimeSpan? debounce = null,
        bool includeTempDirectories = false,
        IReadOnlyCollection<string>? ignoredPaths = null)
    {
        _source = source;
        _trackedRoots = trackedRoots;
        _searchRoots = searchRoots;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        _includeTempDirectories = includeTempDirectories;
        _ignoredPaths = ignoredPaths ?? [];
    }

    public async IAsyncEnumerable<RepositoryChangeEvent> RunAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        lock (_errorsLock)
        {
            _scanErrors = FrozenDictionary<string, string>.Empty;
            _scanWarnings = [];
        }
        var readinessSource = _source as IVolumeReadiness;
        ResetReadiness(readinessSource?.Mode ?? WatchReadinessMode.PumpActive);
        var diagnostics = _source as IVolumeChangeDiagnostics;
        if (diagnostics is not null)
        {
            diagnostics.DriveFailed += OnDriveFailed;
            diagnostics.JournalWarningDetected += OnJournalWarningDetected;
        }
        var scanProgress = _source as IVolumeScanProgress;
        if (scanProgress is not null)
        {
            scanProgress.ScanProgress += OnScanProgress;
        }

        if (readinessSource is not null)
        {
            // Subscribe before anything starts, then fold in the state that already exists: in the
            // coordinator path the arm (and its catch-up) finished before this run began.
            readinessSource.ReadinessChanged += OnVolumeReadinessChanged;
            OnVolumeReadinessChanged(readinessSource.Readiness);
        }
        try
        {
            await foreach (var changeEvent in RunCoreAsync(ct).ConfigureAwait(false))
            {
                yield return changeEvent;
            }
        }
        finally
        {
            MarkTerminal();
            if (readinessSource is not null)
            {
                readinessSource.ReadinessChanged -= OnVolumeReadinessChanged;
            }

            if (diagnostics is not null)
            {
                diagnostics.DriveFailed -= OnDriveFailed;
                diagnostics.JournalWarningDetected -= OnJournalWarningDetected;
            }
            if (scanProgress is not null)
            {
                scanProgress.ScanProgress -= OnScanProgress;
            }
        }
    }

    async IAsyncEnumerable<RepositoryChangeEvent> RunCoreAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Synchronous with the cancel: a pump sitting between its startup flush and its
        // MarkStartupDelivered cannot publish Ready once the consumer has given up.
        await using var terminalOnCancel = ct.Register(MarkTerminal).ConfigureAwait(false);
        var channel = Channel.CreateUnbounded<RepositoryChangeEvent>();
        using var deathSignal = new SourceDeathSignal(_source, reason =>
        {
            MarkTerminal();
            Stopped?.Invoke(reason);
            channel.Writer.TryComplete();
        }, ct);

        Dictionary<string, RepositoryChangeFilter> filtersByVolume;
        try
        {
            filtersByVolume = await BuildFiltersByVolumeAsync(deathSignal.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deathSignal.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            yield break;
        }

        if (_startup is not null)
        {
            InventoryReady?.Invoke(_startup.Inventory);
        }

        Started?.Invoke();
        var pumpTask = PumpAsync(filtersByVolume, channel.Writer, deathSignal.Token);

        try
        {
            await foreach (var changeEvent in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return changeEvent;
            }
        }
        finally
        {
            MarkTerminal();
            // An early consumer exit must stop the pending source read before awaiting the pump.
            await deathSignal.CancelAsync().ConfigureAwait(false);
            await pumpTask.ConfigureAwait(false);
        }
    }

    void OnDriveFailed(string drive, string message)
    {
        lock (_errorsLock)
        {
            if (_scanErrors.TryGetValue(drive, out var previous) && previous == message)
            {
                return;
            }

            var errors = new Dictionary<string, string>(_scanErrors, StringComparer.OrdinalIgnoreCase) { [drive] = message };
            _scanErrors = errors.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
        ScanErrorsChanged?.Invoke();
    }

    void OnScanProgress(VolumeScanProgress sample) => ScanProgress?.Invoke(sample);

    // One slot per drive: the newer report is the current one.
    void OnJournalWarningDetected(JournalWarning warning)
    {
        lock (_errorsLock)
        {
            _scanWarnings = _scanWarnings
                .Where(existing => !string.Equals(existing.DriveLetter, warning.DriveLetter,
                    StringComparison.OrdinalIgnoreCase))
                .Append(warning)
                .ToFrozenSet();
        }
        ScanWarningsChanged?.Invoke();
    }

    void SetArmResult(VolumeArmResult armResult)
    {
        // The arm snapshot is the baseline; the source's current warnings are newer than it
        // whenever a watch-state change landed after the arm or before this run subscribed.
        // Current warnings are read before taking _errorsLock to avoid nested-lock inversion
        // with the source's published-warnings lock during concurrent warning delivery.
        var current = (_source as IVolumeChangeDiagnostics)?.CurrentJournalWarnings ?? [];

        lock (_errorsLock)
        {
            var errors = new Dictionary<string, string>(armResult.Errors, StringComparer.OrdinalIgnoreCase);
            foreach (var (drive, message) in _scanErrors)
            {
                errors[drive] = message;
            }

            _scanErrors = errors.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

            var warnings = armResult.JournalWarnings
                .Concat(current)
                .Concat(_scanWarnings)
                .GroupBy(warning => warning.DriveLetter, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last());
            _scanWarnings = warnings.ToFrozenSet();
        }
        ScanErrorsChanged?.Invoke();
        ScanWarningsChanged?.Invoke();
    }

    // Owns the linked CancellationTokenSource used to fold source death into cancellation.
    // The SourceDied handler is an instance method reading instance fields, not a closure
    // capturing a local that this type later disposes - keeping the source-death wiring out
    // of RunCoreAsync's own scope avoids a disposed-closure hazard there. Takes the death callback
    // directly (rather than exposing its own event) since RunCoreAsync is its only caller and
    // always attaches exactly one handler - a nullable event here would just be an
    // unreachable null-check branch.
    sealed class SourceDeathSignal : IDisposable
    {
        readonly IVolumeChangeSource _source;
        readonly CancellationTokenSource _cts;
        readonly Action<string> _onDied;

        public SourceDeathSignal(IVolumeChangeSource source, Action<string> onDied, CancellationToken ct)
        {
            _source = source;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _onDied = onDied;
            _source.SourceDied += OnSourceDied;
        }

        public CancellationToken Token => _cts.Token;
        public Task CancelAsync() => _cts.CancelAsync();

        void OnSourceDied(string reason) { _onDied(reason); _ = _cts.CancelAsync(); }
        public void Dispose() { _source.SourceDied -= OnSourceDied; _cts.Cancel(); _cts.Dispose(); }
    }

    async Task<Dictionary<string, RepositoryChangeFilter>> BuildFiltersByVolumeAsync(CancellationToken ct)
    {
        var trackedByVolume = GroupRootsByVolume(_trackedRoots);
        var searchByVolume = GroupRootsByVolume(_searchRoots);
        var ignoredByVolume = GroupRootsByVolume(_ignoredPaths);

        IReadOnlyCollection<string> volumes;
        if (_startup is not null)
        {
            volumes = _startup.WatchVolumes;
            SetArmResult(_startup.ArmResult);
        }
        else
        {
            volumes = (string[])[.. trackedByVolume.Keys.Union(searchByVolume.Keys, StringComparer.OrdinalIgnoreCase)];
            var armResult = await _source.ArmAndCatchUpAsync(volumes, ct).ConfigureAwait(false);
            SetArmResult(armResult);
        }

        FrozenDictionary<string, string> armErrors;
        lock (_errorsLock)
        {
            armErrors = _scanErrors;
        }

        SetPumpVolumes((string[])[.. volumes.Where(volume => !armErrors.ContainsKey(volume))]);

        return volumes.ToDictionary(
            volume => volume,
            volume => new RepositoryChangeFilter(
                trackedByVolume.GetValueOrDefault(volume) ?? [],
                searchByVolume.GetValueOrDefault(volume) ?? [],
                ignoredByVolume.GetValueOrDefault(volume) ?? [],
                _includeTempDirectories),
            StringComparer.OrdinalIgnoreCase);
    }

    Dictionary<string, List<string>> GroupRootsByVolume(IReadOnlyCollection<string> roots)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var volume = _startup?.GetObservedVolume(root) ?? GetVolume(root);
            if (volume is null)
            {
                continue;
            }

            if (!result.TryGetValue(volume, out var list))
            {
                result[volume] = list = [];
            }

            list.Add(root);
        }
        return result;
    }

    static string? GetVolume(string path) => WatchPath.Parse(path).Volume;
}
