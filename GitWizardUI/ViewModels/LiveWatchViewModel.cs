using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GitWizard;
using GitWizard.Discovery;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardUI.Services;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.ViewModels;

public sealed partial class LiveWatchViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    // A Deleted(root) is held here, not removed immediately, so a Created event that follows
    // within RenameCorrelationWindowMilliseconds and matches its remote URL can be resolved as a
    // rename instead of an independent delete+add. RepositoryWatchService's own debounce window
    // already coalesces a same-window rename's Deleted+Created into back-to-back events, so this
    // window mainly bridges the rarer case where they land in different debounce flushes.
    const int RenameCorrelationWindowMilliseconds = 300;

    readonly IUiDispatcher _ui;
    readonly RepositoryListViewModel _repositoryList;
    readonly RefreshCoordinator _refresh;
    readonly StatusHeaderViewModel _statusHeader;
    readonly PreparedDiscoveryIndexStore _preparedDiscoveryIndex;
    readonly ErrorLogViewModel _errorLog;
    readonly Dictionary<string, (GitWizardRepository Repository, CancellationTokenSource ExpireCts, long SequenceNumber)> _pendingDeletedRepos = new();
    readonly Dictionary<string, long> _pendingCreateSequenceNumbers = new();
    readonly ScanErrorChangeFilter _reportedLiveScanErrors = new();
    readonly HashSet<string> _inFlightLiveRefreshes = new(StringComparer.OrdinalIgnoreCase);
    long _liveEventSequenceNumber;
    long _liveRunSequenceNumber;
    bool _isLive;
    bool _isLiveStarting;

    internal LiveWatchViewModel(UiEnvironment environment,
        RepositoryListViewModel repositoryList, RefreshCoordinator refresh,
        StatusHeaderViewModel statusHeader, PreparedDiscoveryIndexStore preparedDiscoveryIndex,
        ErrorLogViewModel errorLog)
    {
        _ui = environment.Dispatcher;
        _timeProvider = environment.TimeProvider;
        _repositoryList = repositoryList;
        _refresh = refresh;
        _statusHeader = statusHeader;
        _preparedDiscoveryIndex = preparedDiscoveryIndex;
        _errorLog = errorLog;
        _refresh.PropertyChanged += OnRefreshPropertyChanged;
    }

    void OnRefreshPropertyChanged(object? sender, PropertyChangedEventArgs arguments)
    {
        if (arguments.PropertyName == nameof(RefreshCoordinator.IsRefreshing))
        {
            OnPropertyChanged(nameof(CanToggleLive));
            OnPropertyChanged(nameof(LiveTooltip));
        }
    }

    /// <summary>
    /// True while a <see cref="GitWizardUI.Services.LiveWatchController"/> session is running (elevated USN-journal
    /// watch on Windows), auto-updating the repository list as tracked/search-root repos
    /// change on disk. Toggled via <see cref="ToggleLiveAsync"/>.
    /// </summary>
    public bool IsLive
    {
        get => _isLive;
        internal set
        {
            if (_isLive != value)
            {
                _isLive = value;
                _liveRunSequenceNumber++;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanToggleLive));
                OnPropertyChanged(nameof(LiveButtonText));
                OnPropertyChanged(nameof(LiveTooltip));
            }
        }
    }

    /// <summary>True while the broker is arming and building its per-volume path indexes.</summary>
    public bool IsLiveStarting
    {
        get => _isLiveStarting;
        internal set
        {
            if (_isLiveStarting != value)
            {
                _isLiveStarting = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanToggleLive));
                OnPropertyChanged(nameof(LiveButtonText));
                OnPropertyChanged(nameof(LiveTooltip));
            }
        }
    }

    public string LiveButtonText => IsLiveStarting ? "Starting Live..." : "Live";

    /// <summary>
    /// Live mode may not be started mid-refresh: a full scan rebuilds
    /// <see cref="RepositoryListViewModel.RepositoryMap"/>/<see cref="RepositoryListViewModel.AllRepositories"/>
    /// from scratch, which would race a live event's incremental add/update/remove against that
    /// rebuild. Stopping is always allowed.
    /// On Linux, Live mode requires kernel 5.9+ and supported filesystems via fanotify.
    /// </summary>
    public bool CanToggleLive => IsLive || IsLiveStarting || (!_refresh.IsRefreshing && IsLiveSupported);

    internal LiveWatchController? Controller { get; private set; }

    // Swappable dependency for testability: the real coordinator arms an elevated broker
    // session (a live UAC prompt), which an automated test can never drive. Tests substitute
    // a prepared WatchStartupResult around a fake IVolumeChangeSource to exercise
    // ToggleLiveAsync end-to-end.
    Func<DiscoveryRequest, MftIndexSession?, IProgress<WatchStartupProgress>?, CancellationToken, Task<WatchStartupResult>>? _livePrepareWatchAsync;

    internal Func<DiscoveryRequest, MftIndexSession?, IProgress<WatchStartupProgress>?, CancellationToken, Task<WatchStartupResult>> PrepareWatchAsync
    {
        get => _livePrepareWatchAsync ??= static (request, prepared, progress, cancellationToken) =>
            new RepositoryDiscoveryCoordinator().PrepareWatchAsync(request, prepared, progress, cancellationToken);
        set => _livePrepareWatchAsync = value;
    }

    internal Func<bool> IsElevated { get; set; } = VolumeChangeSourceFactory.IsElevated;

    Func<FanotifyCapabilityResult>? _liveCapabilityProbe;

    internal Func<FanotifyCapabilityResult> CapabilityProbe
    {
        get => _liveCapabilityProbe ??= DefaultLiveCapabilityProbe;
        set
        {
            _liveCapabilityProbe = value;
            OnPropertyChanged(nameof(CanToggleLive));
            OnPropertyChanged(nameof(LiveTooltip));
        }
    }

    public bool IsLiveSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || _liveCapabilityProbe != null
            ? CapabilityProbe().IsSupported
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public string LiveTooltip
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || _liveCapabilityProbe != null)
            {
                return CapabilityProbe().Status switch
                {
                    FanotifyCapabilityStatus.UnsupportedKernel => "Live mode requires Linux kernel 5.9 or newer (unsupported kernel).",
                    FanotifyCapabilityStatus.UnsupportedFileSystem => "Live mode is unsupported on this filesystem (fanotify marks not supported).",
                    _ => "Start or stop live repository monitoring (requires elevation when starting)"
                };
            }

            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "Start or stop live repository monitoring (requires elevation when starting)"
                : "Live mode currently requires Windows or Linux.";
        }
    }

    internal async Task ApplyLiveEventAsync(RepositoryChangeEvent changeEvent)
    {
        var sequenceNumber = Interlocked.Increment(ref _liveEventSequenceNumber);
        if (changeEvent.Kind == RepositoryChangeKind.Changed)
        {
            await ApplyLiveChangedAsync(changeEvent.RepoRoot).ConfigureAwait(false);
        }
        else if (changeEvent.Kind == RepositoryChangeKind.Created)
        {
            await ApplyLiveCreatedAsync(changeEvent.RepoRoot, sequenceNumber).ConfigureAwait(false);
        }
        else if (changeEvent.Kind == RepositoryChangeKind.Deleted)
        {
            ApplyLiveDeleted(changeEvent.RepoRoot, sequenceNumber);
        }
    }

    async Task ApplyLiveChangedAsync(string repoRoot)
    {
        GitWizardRepository? repository = null;
        await _ui.InvokeAsync(() =>
        {
            if (!_inFlightLiveRefreshes.Add(repoRoot))
            {
                return;
            }

            if (_repositoryList.RepositoryMap.TryGetValue(repoRoot, out var node))
            {
                repository = node.Repository;
            }
            else
            {
                _inFlightLiveRefreshes.Remove(repoRoot);
            }
        }).ConfigureAwait(false);

        if (repository is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => repository.Refresh()).ConfigureAwait(false);
        }
        finally
        {
            _ui.Post(() => _inFlightLiveRefreshes.Remove(repoRoot));
        }

        _ui.Post(() => _repositoryList.UpdateCompletedRepository(repository, streaming: _refresh.IsRefreshing));
    }

    async Task ApplyLiveCreatedAsync(string repoRoot, long sequenceNumber)
    {
        await _ui.InvokeAsync(() => _pendingCreateSequenceNumbers[repoRoot] = sequenceNumber).ConfigureAwait(false);
        var repository = new GitWizardRepository(repoRoot);
        await Task.Run(() => repository.Refresh()).ConfigureAwait(false);

        // A short-lived repository (CI/test scratch repos) may already be gone by the time its
        // refresh ran: drop it quietly instead of adding a phantom node.
        if (!Directory.Exists(repoRoot))
        {
            return;
        }

        _ui.Post(() =>
        {
            if (!_pendingCreateSequenceNumbers.TryGetValue(repoRoot, out var pendingSeq) || pendingSeq != sequenceNumber)
            {
                return;
            }

            _pendingCreateSequenceNumbers.Remove(repoRoot);

            if (_pendingDeletedRepos.TryGetValue(repoRoot, out var pending) && pending.SequenceNumber < sequenceNumber)
            {
                _pendingDeletedRepos.Remove(repoRoot);
                _ = CancellationTokenSourceDisposal.CancelAndDisposeAsync(pending.ExpireCts);
            }

            var renamedFrom = TryCorrelateRename(repoRoot, repository);
            if (renamedFrom != null)
            {
                _repositoryList.RemoveRepositoryPathFromUi(renamedFrom);
            }

            _repositoryList.AddRepository(repository, streaming: _refresh.IsRefreshing);
        });
    }

    void ApplyLiveDeleted(string repoRoot, long sequenceNumber)
    {
        _ui.Post(() =>
        {
            if (_pendingCreateSequenceNumbers.TryGetValue(repoRoot, out var pendingSeq) && pendingSeq < sequenceNumber)
            {
                _pendingCreateSequenceNumbers.Remove(repoRoot);
            }

            if (!_repositoryList.RepositoryMap.TryGetValue(repoRoot, out var node))
            {
                return;
            }

            if (_pendingDeletedRepos.TryGetValue(repoRoot, out var existing))
            {
                _ = CancellationTokenSourceDisposal.CancelAndDisposeAsync(existing.ExpireCts);
            }

            var expireCts = new CancellationTokenSource();
            _pendingDeletedRepos[repoRoot] = (node.Repository, expireCts, sequenceNumber);
            _ = ExpirePendingDeleteAsync(repoRoot, sequenceNumber, _liveRunSequenceNumber, expireCts.Token);
        });
    }

    // Checks every still-pending Deleted against the newly Created repo's remote URLs (reusing
    // the same FindRenamedRepo the batch-refresh path uses), cancels the matched entry's expiry
    // timer, and returns the old path so the caller can remove it - or null if this Created is
    // unrelated to any pending delete.
    string? TryCorrelateRename(string repoRoot, GitWizardRepository repository)
    {
        var healthyRepos = new Dictionary<string, GitWizardRepository> { [repoRoot] = repository };
        foreach (var (oldPath, pending) in _pendingDeletedRepos)
        {
            if (RepositoryGroupKeys.FindRenamedRepo(pending.Repository, oldPath, healthyRepos) != repoRoot)
            {
                continue;
            }

            _ = CancellationTokenSourceDisposal.CancelAndDisposeAsync(pending.ExpireCts);
            _pendingDeletedRepos.Remove(oldPath);
            return oldPath;
        }

        return null;
    }

    async Task ExpirePendingDeleteAsync(string repoRoot, long sequenceNumber, long runSequenceNumber, CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromMilliseconds(RenameCorrelationWindowMilliseconds), _timeProvider, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        _ui.Post(() =>
        {
            // Cancellation cannot retract a callback already queued on the dispatcher.
            // Validate both the Live run and the deletion before consuming any state.
            if (!IsLive || _liveRunSequenceNumber != runSequenceNumber ||
                !_pendingDeletedRepos.TryGetValue(repoRoot, out var pending) || pending.SequenceNumber != sequenceNumber)
            {
                return;
            }

            _pendingDeletedRepos.Remove(repoRoot);
            _ = CancellationTokenSourceDisposal.CancelAndDisposeAsync(pending.ExpireCts);
            _repositoryList.RemoveRepositoryPathFromUi(repoRoot);
        });
    }

    void ClearPendingDeletes()
    {
        foreach (var pending in _pendingDeletedRepos.Values)
        {
            _ = CancellationTokenSourceDisposal.CancelAndDisposeAsync(pending.ExpireCts);
        }

        _pendingDeletedRepos.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopLiveAsync().ConfigureAwait(false);
        Controller = null;

        await _ui.InvokeAsync(() =>
        {
            ClearLiveReadiness();
            _liveWatchHistory?.Dispose();
            _liveWatchHistory = null;
        }).ConfigureAwait(false);

        _refresh.PropertyChanged -= OnRefreshPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    static FanotifyCapabilityResult DefaultLiveCapabilityProbe() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? ProbeLinuxCapability()
            : new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);

    [SupportedOSPlatform("linux")]
    static FanotifyCapabilityResult ProbeLinuxCapability()
    {
        try
        {
            return FanotifyInterop.Probe();
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Failed to probe Linux fanotify capabilities.");
            return new FanotifyCapabilityResult(FanotifyCapabilityStatus.UnsupportedKernel, 0);
        }
    }
}
