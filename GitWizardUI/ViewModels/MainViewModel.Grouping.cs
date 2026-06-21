using System.Collections.ObjectModel;
using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    public void ToggleFilter(FilterType filter)
    {
        ActiveFilter = ActiveFilter == filter ? FilterType.None : filter;
        ApplyFilterAndGrouping();
    }

    public void ToggleGroupMode(GroupMode mode)
    {
        ActiveGroupMode = ActiveGroupMode == mode ? GroupMode.None : mode;
        ApplyFilterAndGrouping();
    }

    public void SetSortMode(SortMode mode)
    {
        ActiveSortMode = mode;
        ApplyFilterAndGrouping();
    }

    public void SetSearchText(string text)
    {
        _searchText = text;
        ApplyFilterAndGrouping();
    }

    public void UpdateSearchText(string text) => SetSearchText(text);

    // Debounce search-driven filtering. The SearchText setter (bound two-way to the search box)
    // fires per keystroke, and ApplyFilterAndGrouping does a full off-screen rebuild + collection
    // swap - costly with 700+ repos. Coalesce rapid keystrokes into a single filter pass, mirroring
    // the 200ms debounce the retired MAUI UI did in its SearchBox_TextChanged code-behind. The
    // immediate SetSearchText path is left untouched for programmatic/test callers.
    void DebounceSearch()
    {
        var previous = _searchDebounceCts;
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        previous?.Cancel();
        previous?.Dispose();
        _ = RunSearchDebounceAsync(cts.Token);
    }

    async Task RunSearchDebounceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SearchDebounceMilliseconds, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later keystroke; that one owns the pending filter pass.
            return;
        }

        // ApplyFilterAndGrouping builds off-thread and marshals its UI swap internally, so it is
        // safe to invoke from this thread-pool continuation.
        ApplyFilterAndGrouping();
    }

    // The Avalonia sidebar dispatches by button name; route each through the same
    // Toggle*/SetSortMode entry points the MAUI UI used so behavior matches exactly - clicking the
    // active Filter/Group button clears it, Sort always keeps one active - and so the notifying
    // Active* properties update (the sidebar binds its `.active` highlight class to them).
    public void ApplyFilter(string buttonName) => ToggleFilter(buttonName switch
    {
        "FilterPendingChanges" => FilterType.PendingChanges,
        "FilterSubmoduleCheckout" => FilterType.SubmoduleCheckout,
        "FilterSubmoduleUninitialized" => FilterType.SubmoduleUninitialized,
        "FilterSubmoduleConfigIssue" => FilterType.SubmoduleConfigIssue,
        "FilterDetachedHead" => FilterType.DetachedHead,
        "FilterMyRepositories" => FilterType.MyRepositories,
        "FilterLocalOnlyCommits" => FilterType.LocalOnlyCommits,
        "FilterStale" => FilterType.Stale,
        "FilterDownstreamBranches" => FilterType.DownstreamBranches,
        _ => FilterType.None,
    });

    public void ApplyGroup(string buttonName) => ToggleGroupMode(buttonName switch
    {
        "GroupByDrive" => GroupMode.Drive,
        "GroupByRemoteUrl" => GroupMode.RemoteUrl,
        _ => GroupMode.None,
    });

    public void ApplySort(string buttonName) => SetSortMode(buttonName switch
    {
        "SortByWorkingDirectory" => SortMode.WorkingDirectory,
        "SortByRecentlyUsed" => SortMode.RecentlyUsed,
        "SortByRemoteUrl" => SortMode.RemoteUrl,
        "SortBySizeOnDisk" => SortMode.SizeOnDisk,
        _ => SortMode.WorkingDirectory,
    });

    public Task ClearCacheAsync()
    {
        GitWizardApi.ClearCache();
        return _dialogs.DisplayAlertAsync("Cache Cleared", "Repository cache has been cleared");
    }

    public Task DeleteAllLocalFilesAsync()
    {
        GitWizardApi.DeleteAllLocalFiles();
        return _dialogs.DisplayAlertAsync("Files Deleted", "All local files have been deleted");
    }

    void ApplyFilterAndGrouping()
    {
        _pendingGroups.Clear();

        var filtered = _activeFilter == FilterType.None
            ? _allRepositories
            : _allRepositories.Where(r => r.MatchesFilter(_activeFilter, GlobalUserEmail)).ToList();

        if (!string.IsNullOrWhiteSpace(_searchText))
            filtered = filtered.Where(r => r.WorkingDirectory.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        var sorted = ApplySort(filtered);

        // Build off-screen and swap in one shot to avoid per-item layout updates
        var newCollection = new ObservableCollection<RepositoryNodeViewModel>();

        if (_activeGroupMode == GroupMode.None)
        {
            foreach (var repo in sorted)
                newCollection.Add(repo);
        }
        else
        {
            var groups = GroupRepositories(sorted, _activeGroupMode);

            // For remote URL grouping, only show groups with multiple copies (the duplicates you want to clean up)
            var minGroupSize = _activeGroupMode == GroupMode.RemoteUrl ? 2 : 1;

            // Add group headers (collapsed); children are stored on the header node
            foreach (var group in groups.OrderByDescending(g => g.Value.Count))
            {
                if (group.Value.Count < minGroupSize)
                    continue;

                var header = RepositoryNodeViewModel.CreateGroupHeader(group.Key);
                foreach (var repo in ApplySort(group.Value))
                    header.Children.Add(repo);

                // Update display text now that children are added (so count is correct)
                header.UpdateDisplayText();
                newCollection.Add(header);
            }
        }

        // Swap the collection on the UI thread. The view hooks snapshot/restore the ListBox
        // ScrollViewer offset (whose getter enforces UI-thread affinity), and RefreshAsync reaches
        // this method on a thread-pool thread via its ConfigureAwait(false) continuation - so an
        // inline swap there throws "Call from invalid thread", so the swap must run on the UI
        // thread while the build above stays off-thread to preserve the off-screen-build perf win.
        void SwapInRepositories()
        {
            Repositories = newCollection;
            AfterRepositoriesSwap?.Invoke();
        }

        if (_ui.IsOnUiThread)
            SwapInRepositories();
        else
            _ui.Post(SwapInRepositories);

        UpdateHeaderWithFilterInfo();
    }

    List<RepositoryNodeViewModel> ApplySort(List<RepositoryNodeViewModel> repos)
    {
        return _activeSortMode switch
        {
            SortMode.RecentlyUsed => repos
                .OrderByDescending(r => r.Repository.LastCommitDate ?? DateTimeOffset.MinValue)
                .ToList(),
            SortMode.RemoteUrl => repos
                .OrderBy(r => r.Repository.RemoteUrls.Count > 0
                    ? NormalizeRemoteUrl(r.Repository.RemoteUrls[0])
                    : "\uffff") // sort no-remote to end
                .ToList(),
            SortMode.SizeOnDisk => repos
                .OrderByDescending(r => r.Repository.SizeOnDisk)
                .ToList(),
            _ => repos // WorkingDirectory - already in insertion order (alphabetical from SortedDictionary)
        };
    }

    static Dictionary<string, List<RepositoryNodeViewModel>> GroupRepositories(
        List<RepositoryNodeViewModel> repos, GroupMode mode)
    {
        var groups = new Dictionary<string, List<RepositoryNodeViewModel>>();
        foreach (var repo in repos)
        {
            var keys = GetGroupKeys(repo, mode);
            foreach (var key in keys)
            {
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<RepositoryNodeViewModel>();
                    groups[key] = list;
                }

                list.Add(repo);
            }
        }

        return groups;
    }

    static List<string> GetGroupKeys(RepositoryNodeViewModel repo, GroupMode mode)
    {
        if (mode == GroupMode.Drive)
        {
            var path = repo.WorkingDirectory;
            if (string.IsNullOrEmpty(path))
                return new List<string> { "(unknown)" };

            var root = Path.GetPathRoot(path);
            return new List<string> { string.IsNullOrEmpty(root) ? "(unknown)" : root };
        }

        if (mode == GroupMode.RemoteUrl)
        {
            var urls = repo.Repository.RemoteUrls;
            if (urls.Count == 0)
                return new List<string> { "(no remote)" };

            // Normalize remote URLs for grouping (strip .git suffix, normalize casing)
            return urls.Select(NormalizeRemoteUrl).Distinct().ToList();
        }

        return new List<string> { "(unknown)" };
    }

    static string NormalizeRemoteUrl(string url)
    {
        url = url.Trim();
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        // Normalize SSH URLs (git@github.com:user/repo) to match HTTPS style for grouping
        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colonIndex = url.IndexOf(':');
            if (colonIndex > 0)
            {
                var host = url[4..colonIndex];
                var path = url[(colonIndex + 1)..];
                url = $"{host}/{path}";
            }
        }
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = url[8..];
        }
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            url = url[7..];
        }

        return url.ToLowerInvariant();
    }

    static string? FindRenamedRepo(GitWizardRepository erroredRepo, string oldPath,
        IReadOnlyDictionary<string, GitWizardRepository> healthyRepos)
    {
        if (erroredRepo.RemoteUrls.Count == 0)
            return null;

        foreach (var remoteUrl in erroredRepo.RemoteUrls)
        {
            var normalizedRemote = NormalizeRemoteUrl(remoteUrl);
            foreach (var (path, healthyRepo) in healthyRepos)
            {
                if (path == oldPath)
                    continue;
                foreach (var healthyRemote in healthyRepo.RemoteUrls)
                {
                    if (NormalizeRemoteUrl(healthyRemote) == normalizedRemote)
                        return path;
                }
            }
        }

        return null;
    }

    void AddToGroups(RepositoryNodeViewModel node)
    {
        var keys = GetGroupKeys(node, _activeGroupMode);
        var minGroupSize = _activeGroupMode == GroupMode.RemoteUrl ? 2 : 1;

        foreach (var key in keys)
        {
            // Find existing group header
            RepositoryNodeViewModel? header = null;
            foreach (var candidate in Repositories)
            {
                if (candidate.IsGroupHeader && candidate.GroupKey == key)
                {
                    header = candidate;
                    break;
                }
            }

            if (header != null)
            {
                header.Children.Add(node);
                header.UpdateDisplayText();

                // If expanded, insert the node after the last child in the flat list
                if (header.IsExpanded)
                {
                    var headerIndex = Repositories.IndexOf(header);
                    Repositories.Insert(headerIndex + header.Children.Count, node);
                }
            }
            else
            {
                header = RepositoryNodeViewModel.CreateGroupHeader(key);
                header.Children.Add(node);
                header.UpdateDisplayText();

                // Only show if meets minimum group size
                if (header.Children.Count >= minGroupSize)
                {
                    Repositories.Add(header);
                }
                else
                {
                    // Keep track of it - it might qualify later when more repos arrive
                    // Store it in the collection anyway for remote URL groups so we can
                    // promote it when a second repo arrives
                    _pendingGroups[key] = header;
                }
            }
        }

        // Check if any pending groups now meet the minimum size
        if (_pendingGroups.Count > 0)
        {
            var promoted = new List<string>();
            foreach (var kvp in _pendingGroups)
            {
                if (kvp.Value.Children.Count >= minGroupSize)
                {
                    kvp.Value.UpdateDisplayText();
                    Repositories.Add(kvp.Value);
                    promoted.Add(kvp.Key);
                }
            }

            foreach (var key in promoted)
                _pendingGroups.Remove(key);
        }

        UpdateHeaderWithFilterInfo();
    }

    void UpdateHeaderWithFilterInfo()
    {
        if (_activeFilter == FilterType.None && _activeGroupMode == GroupMode.None)
        {
            HeaderText = _lastRefreshMessage ?? $"{_allRepositories.Count} repositories";
        }
        else if (_activeGroupMode != GroupMode.None)
        {
            var groupCount = Repositories.Count;
            HeaderText = $"{_allRepositories.Count} repositories in {groupCount} groups";
        }
        else
        {
            HeaderText = $"Showing {Repositories.Count} of {_allRepositories.Count} repositories";
        }
    }

}
