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

    sealed class SearchDebouncer : IDisposable
    {
        CancellationTokenSource? _cts;

        public CancellationToken Restart()
        {
            var previous = _cts;
            var next = new CancellationTokenSource();
            _cts = next;
            if (previous != null)
            {
                _ = CancelAndDisposeAsync(previous);
            }
            return next.Token;
        }

        public void Dispose()
        {
            var cts = _cts;
            _cts = null;
            if (cts != null)
            {
                _ = CancelAndDisposeAsync(cts);
            }
        }
    }

    // Debounce search-driven filtering. The SearchText setter (bound two-way to the search box)
    // fires per keystroke, and ApplyFilterAndGrouping does a full off-screen rebuild + collection
    // swap - costly with 700+ repos. Coalesce rapid keystrokes into a single filter pass, mirroring
    // the 200ms debounce the retired MAUI UI did in its SearchBox_TextChanged code-behind. The
    // immediate SetSearchText path is left untouched for programmatic/test callers.
    void DebounceSearch()
    {
        var token = _searchDebouncer.Restart();
        _ = RunSearchDebounceAsync(token);
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
        "FilterBehindRemote" => FilterType.BehindRemote,
        "FilterPublishReady" => FilterType.PublishReady,
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

    public async Task ClearCacheAsync()
    {
        await SetPreparedDiscoveryIndexAsync(null).ConfigureAwait(false);
        GitWizardApi.ClearCache();
        await _dialogs.DisplayAlertAsync("Cache Cleared", "Repository cache has been cleared");
    }

    public async Task DeleteAllLocalFilesAsync()
    {
        await SetPreparedDiscoveryIndexAsync(null).ConfigureAwait(false);
        if (!GitWizardApi.TryDeleteAllLocalFiles(out var refusal))
        {
            await _dialogs.DisplayAlertAsync("Files Not Deleted", refusal);
            return;
        }
        await _dialogs.DisplayAlertAsync("Files Deleted", "All local files have been deleted");
    }

    // suppressAnchorCapture: true only for CompleteRefresh's own final sorted-rebuild call. The
    // anchor for a refresh is captured once, at refresh start, by the window's explicit
    // CaptureScrollAnchor() call before RefreshAsync clears Repositories; that one rebuild's swap
    // must not fire BeforeRepositoriesSwap again and recapture from the rebuilt streaming list,
    // which would overwrite the anchor saved at refresh start. The value is a parameter captured
    // by SwapInRepositories's own closure (not a shared field) so that a second,
    // independently-triggered ApplyFilterAndGrouping call (e.g. a filter/search rebuild whose swap
    // is posted to the UI thread ahead of this one) can never consume this call's suppression or
    // have its own suppressed by it (pr-crew round-9 review of PR #241/#243).
    internal void ApplyFilterAndGrouping(bool suppressAnchorCapture = false)
    {
        _pendingGroups.Clear();

        foreach (var repositoryNode in _allRepositories)
        {
            repositoryNode.ClearParentHeaders();
        }

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
        // The header's repository/group count is computed here, after the swap, rather than as a
        // separate call below: computing it on the calling thread right after posting an async
        // swap would read the OLD Repositories collection, showing a stale group count until some
        // later update happened to refresh it (issue #241 addendum: "953 repositories in 2 groups"
        // while six groups were listed).
        void SwapInRepositories()
        {
            var previousSelectedPath = SelectedRepository?.WorkingDirectory;
            if (!suppressAnchorCapture)
                BeforeRepositoriesSwap?.Invoke();
            Repositories = newCollection;
            if (previousSelectedPath != null)
            {
                SelectedRepository = newCollection.FirstOrDefault(n => n.WorkingDirectory == previousSelectedPath)
                    ?? _allRepositories.FirstOrDefault(n => n.WorkingDirectory == previousSelectedPath);
            }
            UpdateHeaderWithFilterInfo();
            AfterRepositoriesSwap?.Invoke();
        }

        if (_ui.IsOnUiThread)
            SwapInRepositories();
        else
            _ui.Post(SwapInRepositories);
    }

    List<RepositoryNodeViewModel> ApplySort(List<RepositoryNodeViewModel> repos)
    {
        return _activeSortMode switch
        {
            SortMode.WorkingDirectory => repos
                .OrderBy(r => r.WorkingDirectory, WorkingDirectoryComparer)
                .ToList(),
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
            _ => repos,
        };
    }

    // GroupRepositories/GetGroupKeys/NormalizeDriveGroupKey/NormalizeRemoteUrl/FindRenamedRepo
    // live in MainViewModel.GroupKeys.cs.

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

            if (header == null)
            {
                _pendingGroups.TryGetValue(key, out header);
            }

            if (header != null)
            {
                var childIndex = InsertRepositoryNode(header.Children, node);
                header.UpdateDisplayText();

                // If expanded, insert the node at the matching position in the flat list
                if (header.IsExpanded)
                {
                    var headerIndex = Repositories.IndexOf(header);
                    if (headerIndex >= 0)
                    {
                        Repositories.Insert(headerIndex + 1 + childIndex, node);
                    }
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
            // Repositories can also hold an expanded group header's children as flat rows
            // (ToggleGroupExpand / AddToGroups insert them there for display), so the group
            // count must count headers specifically, not every row (issue #241 addendum: the
            // header read "953 repositories in 2 groups" while six groups were listed).
            var groupCount = Repositories.Count(r => r.IsGroupHeader);
            HeaderText = $"{_allRepositories.Count} repositories in {groupCount} groups";
        }
        else
        {
            HeaderText = $"Showing {Repositories.Count} of {_allRepositories.Count} repositories";
        }
    }

}
