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
        _allRepositories.Add(node);

        if (!node.MatchesFilter(_activeFilter, GlobalUserEmail))
            return;

        if (!string.IsNullOrWhiteSpace(_searchText) &&
            !node.WorkingDirectory.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            return;

        if (_activeGroupMode == GroupMode.None)
        {
            Repositories.Add(node);
        }
        else
        {
            AddToGroups(node);
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

        Repositories.Clear();
        _allRepositories.Clear();
        _repositoryMap.Clear();
        _pendingGroups.Clear();

        // Async file I/O for cached repo paths and configuration
        string[]? repositoryPaths = await GitWizardApi.GetCachedRepositoryPathsAsync().ConfigureAwait(false);
        var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync().ConfigureAwait(false);

        var (deletedPaths, renamedOldPaths, nonRepositoryPaths) =
            await RunRefreshScanAsync(configuration, repositoryPaths, fetchRemotes).ConfigureAwait(false);

        UpdateCachedPathsAfterScan(deletedPaths, renamedOldPaths, nonRepositoryPaths);
        RemoveRenamedReposFromUi(renamedOldPaths);

        // Wait for the UI command queue to fully drain before applying grouping/sorting
        while (!_uiCommands.IsEmpty)
            await Task.Delay(150).ConfigureAwait(false);

        // One more delay to let the last batch of UI commands finish processing
        await Task.Delay(200).ConfigureAwait(false);

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
    // non-repo paths.
    async Task<(HashSet<string> DeletedPaths, HashSet<string> RenamedOldPaths, HashSet<string> NonRepositoryPaths)> RunRefreshScanAsync(
        GitWizardConfiguration configuration, string[]? repositoryPaths, bool fetchRemotes)
    {
        var deletedPaths = new HashSet<string>();
        var renamedOldPaths = new HashSet<string>();
        var nonRepositoryPaths = new HashSet<string>();

        await Task.Run(async () =>
        {
            _stopwatch.Restart();
            var report = await GitWizardReport.GenerateReportAsync(configuration, repositoryPaths, this,
                new GitWizardReportOptions { FetchRemotes = fetchRemotes, DeepRefresh = fetchRemotes });
            _stopwatch.Stop();

            // Capture deleted and stale non-repo paths for cache cleanup on main thread
            deletedPaths = new HashSet<string>(report.DeletedPaths);
            nonRepositoryPaths = new HashSet<string>(report.NonRepositoryPaths);

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

        return (deletedPaths, renamedOldPaths, nonRepositoryPaths);
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
        foreach (var path in renamedOldPaths)
            RemoveRepositoryByPath(path);
    }

    /// <summary>
    /// Removes a single repository, by working-directory path, from every UI collection
    /// (<c>_repositoryMap</c>, <c>_allRepositories</c>, <see cref="Repositories"/>). No-op if the
    /// path isn't currently tracked. Shared by the batch-refresh rename cleanup above and
    /// Live mode's per-event deletion/rename handling in <c>MainViewModel.Live.cs</c>.
    /// </summary>
    internal void RemoveRepositoryByPath(string path)
    {
        if (!_repositoryMap.TryGetValue(path, out var node))
            return;

        _repositoryMap.TryRemove(path, out _);
        _allRepositories.Remove(node);
        Repositories.Remove(node);
    }

}
