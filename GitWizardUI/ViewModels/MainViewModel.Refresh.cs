using System.Diagnostics;
using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    internal void AddRepository(GitWizardRepository repository)
    {
        // The first repository to surface ends the "Scanning…" gap; rows now stream into the list.
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
                Repositories.Add(node);
        }
        else
        {
            AddToGroups(node);
        }
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
                Repositories.Add(node);

            UpdateHeaderWithFilterInfo();
        }
        // When grouping is active, update the parent group header to reflect error/warning counts
        if (_activeGroupMode != GroupMode.None)
        {
            foreach (var item in Repositories)
            {
                if (item.IsGroupHeader && item.Children.Contains(node))
                {
                    item.UpdateDisplayText();
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Hard refresh: clear the cached repository list and report, then refresh.
    /// With no cache present, the refresh runs a full MFT discovery scan, which
    /// self-elevates via UAC on Windows. Bound to Shift+click on the Refresh button.
    /// </summary>
    public async Task HardRefreshAsync()
    {
        if (IsRefreshing)
            return;

        GitWizardApi.ClearCache();
        await RefreshAsync(background: false);
    }

    public async Task RefreshAsync(bool background = false, bool fetchRemotes = false)
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
        string[]? repositoryPaths = await GitWizardApi.GetCachedRepositoryPathsAsync().ConfigureAwait(false);
        var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync().ConfigureAwait(false);

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
            PrePopulateFromReport(configuration, repositoryPaths);
        }).ConfigureAwait(false);

        var (deletedPaths, renamedOldPaths, nonRepositoryPaths, freshPaths) =
            await RunRefreshScanAsync(configuration, repositoryPaths, fetchRemotes).ConfigureAwait(false);

        UpdateCachedPathsAfterScan(deletedPaths, renamedOldPaths, nonRepositoryPaths);

        // Wait for the UI command queue to fully drain before pruning. RepositoryCreated
        // commands are queued before a refresh determines that a path is no longer a repo,
        // so pruning earlier could let a late command restore a stale row.
        while (!_uiCommands.IsEmpty)
            await Task.Delay(150).ConfigureAwait(false);

        await _ui.InvokeAsync(() =>
        {
            PruneStalePrePopulatedRepos(freshPaths, renamedOldPaths);
            RemoveRenamedReposFromUi(renamedOldPaths);
        }).ConfigureAwait(false);

        ApplyFilterAndGrouping();

        if (_activeFilter == FilterType.None && _activeGroupMode == GroupMode.None)
            HeaderText = _lastRefreshMessage ?? $"{_allRepositories.Count} repositories";
        else
            UpdateHeaderWithFilterInfo();

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
    // non-repo paths, plus the set of healthy repo paths discovered by the scan.
    async Task<(HashSet<string> DeletedPaths, HashSet<string> RenamedOldPaths, HashSet<string> NonRepositoryPaths, HashSet<string> FreshPaths)> RunRefreshScanAsync(
        GitWizardConfiguration configuration, string[]? repositoryPaths, bool fetchRemotes)
    {
        var deletedPaths = new HashSet<string>();
        var renamedOldPaths = new HashSet<string>();
        var nonRepositoryPaths = new HashSet<string>();
        var freshPaths = new HashSet<string>();

        await Task.Run(async () =>
        {
            _stopwatch.Restart();
            var report = await GitWizardReport.GenerateReportAsync(configuration, repositoryPaths, this,
                new GitWizardReportOptions { FetchRemotes = fetchRemotes, DeepRefresh = fetchRemotes });
            _stopwatch.Stop();

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
            _lastRefreshMessage = refreshMsg;

            if (repositoryPaths == null)
                GitWizardApi.SaveCachedRepositoryPaths(report.GetRepositoryPaths());
        }).ConfigureAwait(false);

        return (deletedPaths, renamedOldPaths, nonRepositoryPaths, freshPaths);
    }

    // Pre-populate the UI from the cached report so repos appear immediately on launch.
    // Existing repos get updated in-place as the refresh completes; new repos discovered
    // during the scan get added normally through OnRepositoryCreated. (Issue #98.)
    internal void PrePopulateFromReport(GitWizardConfiguration configuration, string[]? repositoryPaths)
    {
        // These collections describe only the current refresh cycle. Reset them before
        // every early return so paths from a prior refresh cannot suppress fresh rows.
        _prePopulatedNodes = null;
        _prePopulatedPaths.Clear();

        if (repositoryPaths == null || repositoryPaths.Length == 0)
            return;

        // Load the cached report (may be null if no cache exists).
        var cachedReport = GitWizardReport.GetCachedReport();
        if (cachedReport == null || cachedReport.Repositories.Count == 0)
            return;

        var pathsSet = new HashSet<string>(repositoryPaths);

        foreach (var (path, repo) in cachedReport.Repositories)
        {
            if (!pathsSet.Contains(path))
                continue;

            var node = new RepositoryNodeViewModel(repo);
            _repositoryMap[path] = node;
            _allRepositories.Add(node);
            _prePopulatedPaths.Add(path);
            _prePopulatedNodes ??= new HashSet<RepositoryNodeViewModel>();
            _prePopulatedNodes.Add(node);

            if (!node.MatchesFilter(_activeFilter, GlobalUserEmail))
                continue;
            if (!string.IsNullOrWhiteSpace(_searchText) &&
                !path.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                continue;

            if (_activeGroupMode == GroupMode.None)
                Repositories.Add(node);
            else
                AddToGroups(node);
        }
    }

    // After a full refresh, prune repos that were pre-populated but no longer exist
    // in the fresh report (deleted, renamed, or stale non-repo paths).  _allRepositories
    // is cleared at the start of RefreshAsync, so this method only handles cleanup
    // from Repositories and _repositoryMap for nodes whose paths are absent from the
    // fresh scan results (deleted) or that moved (renamed).
    internal void PruneStalePrePopulatedRepos(HashSet<string> freshPaths, HashSet<string> renamedOldPaths)
    {
        if (_prePopulatedNodes == null)
            return;

        var stalePaths = _prePopulatedNodes
            .Select(node => node.WorkingDirectory)
            .Where(path => !freshPaths.Contains(path) || renamedOldPaths.Contains(path))
            .ToList();

        foreach (var path in stalePaths)
            RemoveRepositoryPathFromUi(path);

        _prePopulatedNodes = null;
        _prePopulatedPaths.Clear();
    }

    // Drop deleted, renamed (old path), and stale non-repo entries from the cached
    // repository-path list.
    internal static void UpdateCachedPathsAfterScan(HashSet<string> deletedPaths, HashSet<string> renamedOldPaths,
        HashSet<string> nonRepositoryPaths)
    {
        var cachedPaths = GitWizardApi.GetCachedRepositoryPaths();
        if (cachedPaths == null)
            return;

        var pathsToRemove = deletedPaths.Union(renamedOldPaths).Union(nonRepositoryPaths).ToHashSet();
        if (pathsToRemove.Count > 0)
        {
            var updatedPaths = cachedPaths.Where(p => !pathsToRemove.Contains(p)).ToList();
            GitWizardApi.SaveCachedRepositoryPaths(updatedPaths);
        }
    }

    // Remove renamed repos (old path entries) from the UI collections.
    void RemoveRenamedReposFromUi(HashSet<string> renamedOldPaths)
    {
        if (renamedOldPaths.Count == 0)
            return;

        foreach (var path in renamedOldPaths)
            RemoveRepositoryPathFromUi(path);
    }

    void RemoveRepositoryPathFromUi(string path)
    {
        _repositoryMap.TryRemove(path, out _);
        _allRepositories.RemoveAll(candidate => candidate.WorkingDirectory == path);
        _prePopulatedPaths.Remove(path);

        var groupHeaders = Repositories.Where(candidate => candidate.IsGroupHeader)
            .Concat(_pendingGroups.Values).Distinct().ToList();

        for (var index = Repositories.Count - 1; index >= 0; index--)
        {
            if (Repositories[index].WorkingDirectory == path)
                Repositories.RemoveAt(index);
        }

        foreach (var groupHeader in groupHeaders)
        {
            for (var index = groupHeader.Children.Count - 1; index >= 0; index--)
            {
                if (groupHeader.Children[index].WorkingDirectory == path)
                    groupHeader.Children.RemoveAt(index);
            }

            groupHeader.UpdateDisplayText();
        }
    }
}
