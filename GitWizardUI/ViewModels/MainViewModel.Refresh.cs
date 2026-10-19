using System.Diagnostics;
using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    // Test seam: lets a test drive refresh discovery through a coordinator backed by fake
    // dependencies instead of the real platform index. Null in production.
    internal RepositoryDiscoveryCoordinator? RefreshDiscoveryCoordinator { get; set; }

    internal void AddRepository(GitWizardRepository repository)
    {
        // The first repository to surface ends the "Scanning..." gap; rows now stream into the list.
        IsScanning = false;

        var path = repository.WorkingDirectory;
        if (string.IsNullOrEmpty(path))
            return;

        var node = new RepositoryNodeViewModel(repository);
        _repositoryMap[path] = node;

        var existingNode = _allRepositories.FirstOrDefault(candidate => candidate.WorkingDirectory == path);
        if (existingNode != null)
        {
            var existingIndex = _allRepositories.IndexOf(existingNode);
            _allRepositories[existingIndex] = node;
            ReplaceVisibleRepositoryNode(existingNode, node);
            return;
        }

        _allRepositories.Add(node);

        if (!node.MatchesFilter(_activeFilter, GlobalUserEmail))
            return;

        if (!string.IsNullOrWhiteSpace(_searchText) &&
            !node.WorkingDirectory.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            return;

        if (_activeGroupMode == GroupMode.None)
        {
            if (!Repositories.Any(candidate => candidate.WorkingDirectory == path))
            {
                if (IsRefreshing)
                    Repositories.Add(node);
                else
                    InsertRepositoryNode(Repositories, node);
            }
        }
        else
        {
            if (IsRefreshing)
            {
                foreach (var key in GetGroupKeys(node, _activeGroupMode))
                    MergeGroupBatch(key, new List<RepositoryNodeViewModel> { node }, sort: false);
            }
            else
            {
                AddToGroups(node);
            }
        }
    }

    // Bulk equivalent of AddRepository for a drained batch of arrivals; see
    // MainViewModel.RefreshBatch.cs for why and how (round-2 pr-crew review of PR #241/#243).
    internal void AddRepositoriesBatch(IReadOnlyList<GitWizardRepository> repositories)
    {
        if (repositories.Count == 0)
            return;

        if (repositories.Count == 1 && !IsRefreshing)
        {
            AddRepository(repositories[0]);
            return;
        }

        IsScanning = false;

        var newNodesByPath = ResolveBatchAgainstExistingRepositories(repositories);
        if (newNodesByPath.Count > 0)
            MergeNewNodesIntoDisplay(newNodesByPath.Values);
    }

    void ReplaceVisibleRepositoryNode(RepositoryNodeViewModel existingNode, RepositoryNodeViewModel freshNode)
    {
        var visibleIndex = Repositories.IndexOf(existingNode);
        if (visibleIndex >= 0)
            Repositories[visibleIndex] = freshNode;

        foreach (var groupHeader in Repositories.Where(candidate => candidate.IsGroupHeader)
                     .Concat(_pendingGroups.Values).Distinct())
        {
            var childIndex = groupHeader.Children.IndexOf(existingNode);
            if (childIndex < 0)
                continue;

            groupHeader.Children[childIndex] = freshNode;
            groupHeader.UpdateDisplayText();
        }
    }

    void AddSubmodule(GitWizardRepository parent, GitWizardRepository submodule)
    {
        var parentPath = parent.WorkingDirectory;
        if (string.IsNullOrEmpty(parentPath) || !_repositoryMap.TryGetValue(parentPath, out var parentNode))
            return;

        var submodulePath = submodule.WorkingDirectory;
        if (string.IsNullOrEmpty(submodulePath))
            return;

        var submoduleNode = new RepositoryNodeViewModel(submodule);
        _repositoryMap[submodulePath] = submoduleNode;
        parentNode.Children.Add(submoduleNode);
    }

    static void AddUninitializedSubmodule(GitWizardRepository parent, string submodulePath)
    {
        // Uninitialized submodules are intentionally not shown in the tree view yet; they could be
        // added as a special node type in the future. Record the skip for verbose diagnostics.
        GitWizardLog.Log($"Skipping uninitialized submodule {submodulePath} under {parent.WorkingDirectory}",
            GitWizardLog.LogType.Verbose);
    }

    internal void UpdateCompletedRepository(GitWizardRepository repository)
    {
        var path = repository.WorkingDirectory;

        if (!string.IsNullOrEmpty(repository.RefreshError))
            LogError(repository.RefreshError, path);

        if (string.IsNullOrEmpty(path) || !_repositoryMap.TryGetValue(path, out var node))
            return;

        node.Update();

        // For filtering (no grouping): incrementally add/remove without rebuilding
        if (_activeFilter != FilterType.None && _activeGroupMode == GroupMode.None)
        {
            var isShown = Repositories.Contains(node);
            var shouldShow = node.MatchesFilter(_activeFilter, GlobalUserEmail);
            if (isShown && !shouldShow)
                Repositories.Remove(node);
            else if (!isShown && shouldShow)
            {
                if (IsRefreshing)
                    Repositories.Add(node);
                else
                    InsertRepositoryNode(Repositories, node);
            }

            UpdateHeaderWithFilterInfo();
        }
        // A repository can appear in multiple remote groups; refresh every aggregate and status summary.
        if (_activeGroupMode != GroupMode.None)
        {
            foreach (var header in node.ParentHeaders)
            {
                header.UpdateDisplayText();
            }
        }
    }

    /// <summary>
    /// Hard refresh: force a fresh discovery scan (rebuild purpose), then refresh. The cached
    /// repository list and report are left in place until the scan's outcome is known - an
    /// unusable or partial discovery preserves them rather than clearing them up front.
    /// Bound to Shift+click on the Refresh button.
    /// </summary>
    public async Task HardRefreshAsync()
    {
        if (IsRefreshing)
            return;

        await RefreshAsync(background: false, rebuild: true).ConfigureAwait(false);
    }

    public async Task RefreshAsync(bool background = false, bool fetchRemotes = false, bool rebuild = false)
    {
        if (IsRefreshing)
            return;

        IsRefreshing = true;
        IsScanning = true;
        HeaderText = fetchRemotes ? "Fetching remotes and refreshing..." : "Refreshing...";

        await EnsureGlobalUserEmailAsync();

        // Read cached repo paths and config first (lightweight file I/O).
        // Pre-populate UI from cached report so repos appear immediately while the
        // full refresh runs in the background. This eliminates the 30+ second empty-list
        // gap on large repo sets where individual refreshes take a long time (issue #98).
        var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync().ConfigureAwait(false);
        var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync().ConfigureAwait(false);
        var cachedReport = await GitWizardReport.GetCachedReportAsync().ConfigureAwait(false);

        // A rebuild runs discovery instead of re-checking the cached paths; the cached paths
        // still drive pre-population, and the coordinator's inventory decides what is persisted.
        var repositoryPaths = rebuild ? null : cachedPaths;

        // Clear displayed list, group state, and the backing collections so that only
        // nodes from the cached report (pre-population) + fresh scan (AddRepository)
        // survive. This prevents stale or deleted repos from accumulating across
        // repeated refresh cycles. The visible collection must be changed on the UI thread.
        await _ui.InvokeAsync(() =>
        {
            Repositories.Clear();
            _pendingGroups.Clear();
            _allRepositories.Clear();
            _repositoryMap.Clear();
            PrePopulateFromReport(configuration, cachedPaths, cachedReport);
        }).ConfigureAwait(false);

        var (deletedPaths, renamedOldPaths, nonRepositoryPaths, freshPaths, publishable) =
            await RunRefreshScanAsync(configuration, repositoryPaths, fetchRemotes, rebuild).ConfigureAwait(false);

        await UpdateCachedPathsAfterScanAsync(deletedPaths, renamedOldPaths, nonRepositoryPaths).ConfigureAwait(false);

        // Wait for the UI command queue to fully drain before pruning. RepositoryCreated
        // commands are queued before a refresh determines that a path is no longer a repo,
        // so pruning earlier could let a late command restore a stale row.
        if (!await WaitForUICommandsToDrainAsync().ConfigureAwait(false))
            return;

        await _ui.InvokeAsync(() =>
        {
            // Only a publishable inventory observed every configured root, so only then may
            // pre-populated rows be pruned for missing from the fresh scan. An incomplete
            // discovery retains the rows its scope did not cover.
            if (publishable)
                PruneStalePrePopulatedRepos(freshPaths, renamedOldPaths);
            RemoveRenamedReposFromUi(renamedOldPaths);
        }).ConfigureAwait(false);

        // CompleteRefresh runs one sorted rebuild (ApplyFilterAndGrouping) and swaps in the sorted
        // collection while preserving selection and scroll position.
        CompleteRefresh();
    }

    internal void CompleteRefresh()
    {
        // The anchor for this refresh was already captured once, at refresh start, before
        // Repositories was cleared; this final rebuild's own swap must not recapture it from the
        // just-streamed, still-unsorted list. suppressAnchorCapture is a parameter captured by this
        // one call's SwapInRepositories closure, so an unrelated ApplyFilterAndGrouping call (e.g.
        // a filter/search rebuild) can never consume or be affected by it.
        ApplyFilterAndGrouping(suppressAnchorCapture: true);
        IsScanning = false;
        IsRefreshing = false;
    }

    // Read the global git user.email once per session for the "My Repositories" filter.
    static async Task EnsureGlobalUserEmailAsync()
    {
        if (GlobalUserEmail != null)
            return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "config --global user.email",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
                GlobalUserEmail = (await process.StandardOutput.ReadToEndAsync()).Trim();
        }
        catch (Exception exception)
        {
            // Could not read global git user.email; the email filter just will not match.
            GitWizardLog.Log($"Could not read global git user.email: {exception.Message}", GitWizardLog.LogType.Verbose);
        }
    }

    // Run the CPU-bound git scan on the thread pool; returns the deleted, renamed-old, and stale
    // non-repo paths, the set of healthy repo paths discovered by the scan, and whether the
    // discovery inventory may publish a full inventory (false for partial/unusable outcomes).
    async Task<(HashSet<string> DeletedPaths, HashSet<string> RenamedOldPaths, HashSet<string> NonRepositoryPaths, HashSet<string> FreshPaths, bool Publishable)> RunRefreshScanAsync(
        GitWizardConfiguration configuration, string[]? repositoryPaths, bool fetchRemotes, bool rebuild = false)
    {
        var deletedPaths = new HashSet<string>();
        var renamedOldPaths = new HashSet<string>();
        var nonRepositoryPaths = new HashSet<string>();
        var freshPaths = new HashSet<string>();
        var publishable = true;

        await Task.Run(async () =>
        {
            _stopwatch.Restart();
            await using var report = await GitWizardReport.GenerateReportAsync(configuration, repositoryPaths, this,
                new GitWizardReportOptions
                {
                    FetchRemotes = fetchRemotes,
                    DeepRefresh = fetchRemotes,
                    RetainDiscoveryIndex = true,
                    DiscoveryPurpose = rebuild ? DiscoveryPurpose.Rebuild : DiscoveryPurpose.Ordinary,
                    DiscoveryCoordinator = RefreshDiscoveryCoordinator
                }).ConfigureAwait(false);
            _stopwatch.Stop();

            await SetPreparedDiscoveryIndexAsync(report.DetachDiscoveryIndex()).ConfigureAwait(false);

            publishable = report.DiscoveryInventory is not { CanPublishFullInventory: false };

            // Capture deleted and stale non-repo paths for cache cleanup on main thread
            deletedPaths = new HashSet<string>(report.DeletedPaths);
            nonRepositoryPaths = new HashSet<string>(report.NonRepositoryPaths);
            freshPaths = new HashSet<string>(report.Repositories.Keys);

            // Detect renamed repos: find errored repos whose remote URLs
            // match a newly discovered (non-error) repo at a different path
            var erroredRepos = report.Repositories
                .Where(kvp => kvp.Value.RefreshError != null)
                .ToList();
            var healthyRepos = report.Repositories
                .Where(kvp => kvp.Value.RefreshError == null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            foreach (var (path, repo) in erroredRepos)
            {
                var newHealthyPath = FindRenamedRepo(repo, path, healthyRepos);
                if (newHealthyPath != null && newHealthyPath != path)
                {
                    renamedOldPaths.Add(path);
                    GitWizardLog.Log($"Repository renamed: {path} -> {newHealthyPath}");
                }
            }

            var refreshMsg = $"Refresh completed in {(float)_stopwatch.ElapsedMilliseconds / 1000:F2} seconds";
            if (deletedPaths.Count > 0)
                refreshMsg += $", {deletedPaths.Count} deleted";
            if (renamedOldPaths.Count > 0)
                refreshMsg += $", {renamedOldPaths.Count} renamed";
            if (nonRepositoryPaths.Count > 0)
                refreshMsg += $", {nonRepositoryPaths.Count} stale";
            if (!publishable)
            {
                refreshMsg += " - discovery incomplete; previous entries preserved";
                LogError("Discovery was incomplete; the previous repository list and report were preserved.");
            }
            _lastRefreshMessage = refreshMsg;

            // Only a publishable discovery may replace the cached list; an unusable or partial
            // outcome leaves the coordinator's reconciled list (and the prior entries) intact.
            if (repositoryPaths == null && publishable)
                await GitWizardApi.SaveCachedRepositoryPathsAsync(report.GetRepositoryPaths()).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return (deletedPaths, renamedOldPaths, nonRepositoryPaths, freshPaths, publishable);
    }

    // Pre-populate the UI from the cached report so repos appear immediately on launch.
    // Existing repos get updated in-place as the refresh completes; new repos discovered
    // during the scan get added normally through OnRepositoryCreated. (Issue #98.)
    internal void PrePopulateFromReport(GitWizardConfiguration configuration, string[]? repositoryPaths,
        GitWizardReport? cachedReport)
    {
        // These collections describe only the current refresh cycle. Reset them before
        // every early return so paths from a prior refresh cannot suppress fresh rows.
        _prePopulatedNodes = null;

        if (repositoryPaths == null || repositoryPaths.Length == 0 || cachedReport == null || cachedReport.Repositories.Count == 0)
            return;

        var pathsSet = new HashSet<string>(repositoryPaths);

        // Both the ungrouped display batch and each group's batch are collected here and applied
        // once at the end (SortBatchForDisplay / MergeGroupBatch) instead of inserting or calling
        // AddToGroups per row: this loop can process hundreds of cached rows in one synchronous
        // burst, and a per-row insertion costs an O(n) scan-and-shift of the already-inserted,
        // growing list on every iteration - quadratic over the whole batch (pr-crew review of PR
        // #241/#243, rounds 1 and 2). PrePopulateFromReport always runs against empty
        // Repositories/_pendingGroups (RefreshAsync clears both immediately before calling it), so
        // MergeGroupBatch's fresh-header path is what actually runs here in practice.
        List<RepositoryNodeViewModel>? ungroupedBatch = null;
        Dictionary<string, List<RepositoryNodeViewModel>>? groupedBatches = null;

        foreach (var (path, repo) in cachedReport.Repositories)
        {
            if (!pathsSet.Contains(path))
                continue;

            var node = new RepositoryNodeViewModel(repo);
            _repositoryMap[path] = node;
            _allRepositories.Add(node);
            _prePopulatedNodes ??= new HashSet<RepositoryNodeViewModel>();
            _prePopulatedNodes.Add(node);

            if (!node.MatchesFilter(_activeFilter, GlobalUserEmail))
                continue;
            if (!string.IsNullOrWhiteSpace(_searchText) &&
                !path.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                continue;

            if (_activeGroupMode == GroupMode.None)
            {
                ungroupedBatch ??= new List<RepositoryNodeViewModel>();
                ungroupedBatch.Add(node);
            }
            else
            {
                foreach (var key in GetGroupKeys(node, _activeGroupMode))
                {
                    groupedBatches ??= new Dictionary<string, List<RepositoryNodeViewModel>>();
                    if (!groupedBatches.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<RepositoryNodeViewModel>();
                        groupedBatches[key] = bucket;
                    }

                    bucket.Add(node);
                }
            }
        }

        if (ungroupedBatch != null)
        {
            foreach (var node in SortBatchForDisplay(ungroupedBatch))
                Repositories.Add(node);
        }

        if (groupedBatches != null)
        {
            foreach (var (key, bucket) in groupedBatches)
                MergeGroupBatch(key, bucket);
        }
    }
}
