using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardUI.Services;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    // A Deleted(root) is held here, not removed immediately, so a Created event that follows
    // within RenameCorrelationWindowMilliseconds and matches its remote URL can be resolved as a
    // rename instead of an independent delete+add. RepositoryWatchService's own debounce window
    // already coalesces a same-window rename's Deleted+Created into back-to-back events, so this
    // window mainly bridges the rarer case where they land in different debounce flushes.
    const int RenameCorrelationWindowMilliseconds = 300;

    readonly Dictionary<string, (GitWizardRepository Repository, CancellationTokenSource ExpireCts, long SequenceNumber)> _pendingDeletedRepos = new();
    readonly Dictionary<string, long> _pendingCreateSequenceNumbers = new();
    readonly Dictionary<string, string> _reportedLiveScanErrors = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _inFlightLiveRefreshes = new(StringComparer.OrdinalIgnoreCase);
    long _liveEventSequenceNumber;

    LiveWatchController? _liveController;

    // Swappable dependency for testability: the real coordinator arms an elevated broker
    // session (a live UAC prompt), which an automated test can never drive. Tests substitute
    // a prepared WatchStartupResult around a fake IVolumeChangeSource to exercise
    // ToggleLiveAsync end-to-end.
    Func<DiscoveryRequest, MftIndexSession?, IProgress<WatchStartupProgress>?, CancellationToken, Task<WatchStartupResult>>? _livePrepareWatchAsync;

    internal Func<DiscoveryRequest, MftIndexSession?, IProgress<WatchStartupProgress>?, CancellationToken, Task<WatchStartupResult>> LivePrepareWatchAsync
    {
        get => _livePrepareWatchAsync ??= static (request, prepared, progress, cancellationToken) =>
            new RepositoryDiscoveryCoordinator().PrepareWatchAsync(request, prepared, progress, cancellationToken);
        set => _livePrepareWatchAsync = value;
    }

    internal Func<bool> LiveIsElevated { get; set; } = VolumeChangeSourceFactory.IsElevated;

    Func<FanotifyCapabilityResult>? _liveCapabilityProbe;

    internal Func<FanotifyCapabilityResult> LiveCapabilityProbe
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
            ? LiveCapabilityProbe().IsSupported
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public string LiveTooltip
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || _liveCapabilityProbe != null)
            {
                return LiveCapabilityProbe().Status switch
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

    internal void ApplyLiveEvent(RepositoryChangeEvent ev) => _ = ApplyLiveEventAsync(ev);

    internal async Task ApplyLiveEventAsync(RepositoryChangeEvent ev)
    {
        var sequenceNumber = Interlocked.Increment(ref _liveEventSequenceNumber);
        if (ev.Kind == RepositoryChangeKind.Changed)
            await ApplyLiveChangedAsync(ev.RepoRoot).ConfigureAwait(false);
        else if (ev.Kind == RepositoryChangeKind.Created)
            await ApplyLiveCreatedAsync(ev.RepoRoot, sequenceNumber).ConfigureAwait(false);
        else if (ev.Kind == RepositoryChangeKind.Deleted)
            ApplyLiveDeleted(ev.RepoRoot, sequenceNumber);
    }

    async Task ApplyLiveChangedAsync(string repoRoot)
    {
        GitWizardRepository? repository = null;
        await _ui.InvokeAsync(() =>
        {
            if (!_inFlightLiveRefreshes.Add(repoRoot))
                return;

            if (_repositoryMap.TryGetValue(repoRoot, out var node))
                repository = node.Repository;
            else
                _inFlightLiveRefreshes.Remove(repoRoot);
        }).ConfigureAwait(false);

        if (repository is null)
            return;

        try
        {
            await Task.Run(() => repository.Refresh()).ConfigureAwait(false);
        }
        finally
        {
            _ui.Post(() => _inFlightLiveRefreshes.Remove(repoRoot));
        }

        _ui.Post(() => UpdateCompletedRepository(repository));
    }

    async Task ApplyLiveCreatedAsync(string repoRoot, long sequenceNumber)
    {
        await _ui.InvokeAsync(() => _pendingCreateSequenceNumbers[repoRoot] = sequenceNumber).ConfigureAwait(false);
        var repository = new GitWizardRepository(repoRoot);
        await Task.Run(() => repository.Refresh()).ConfigureAwait(false);

        // A short-lived repository (CI/test scratch repos) may already be gone by the time its
        // refresh ran: drop it quietly instead of adding a phantom node.
        if (!Directory.Exists(repoRoot))
            return;

        _ui.Post(() =>
        {
            if (!_pendingCreateSequenceNumbers.TryGetValue(repoRoot, out var pendingSeq) || pendingSeq != sequenceNumber)
                return;
            _pendingCreateSequenceNumbers.Remove(repoRoot);

            if (_pendingDeletedRepos.TryGetValue(repoRoot, out var pending) && pending.SequenceNumber < sequenceNumber)
            {
                _pendingDeletedRepos.Remove(repoRoot);
                _ = CancelAndDisposeAsync(pending.ExpireCts);
            }

            var renamedFrom = TryCorrelateRename(repoRoot, repository);
            if (renamedFrom != null)
                RemoveRepositoryByPath(renamedFrom);

            AddRepository(repository);
        });
    }

    void ApplyLiveDeleted(string repoRoot, long sequenceNumber)
    {
        _ui.Post(() =>
        {
            if (_pendingCreateSequenceNumbers.TryGetValue(repoRoot, out var pendingSeq) && pendingSeq < sequenceNumber)
                _pendingCreateSequenceNumbers.Remove(repoRoot);

            if (!_repositoryMap.TryGetValue(repoRoot, out var node))
                return;

            if (_pendingDeletedRepos.TryGetValue(repoRoot, out var existing))
                _ = CancelAndDisposeAsync(existing.ExpireCts);

            var expireCts = new CancellationTokenSource();
            _pendingDeletedRepos[repoRoot] = (node.Repository, expireCts, sequenceNumber);
            _ = ExpirePendingDeleteAsync(repoRoot, expireCts.Token);
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
            if (FindRenamedRepo(pending.Repository, oldPath, healthyRepos) != repoRoot)
                continue;

            _ = CancelAndDisposeAsync(pending.ExpireCts);
            _pendingDeletedRepos.Remove(oldPath);
            return oldPath;
        }

        return null;
    }

    async Task ExpirePendingDeleteAsync(string repoRoot, CancellationToken token)
    {
        try { await Task.Delay(RenameCorrelationWindowMilliseconds, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        _ui.Post(() =>
        {
            if (_pendingDeletedRepos.Remove(repoRoot))
                RemoveRepositoryByPath(repoRoot);
        });
    }

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
