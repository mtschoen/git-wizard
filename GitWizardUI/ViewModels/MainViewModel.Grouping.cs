using System.Collections.ObjectModel;
using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    // Sidebar semantics: clicking the active Filter/Group button clears it; Sort always keeps one
    // mode active. The notifying Active* properties drive the sidebar's `.active` highlight class.
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
                _ = CancellationTokenSourceDisposal.CancelAndDisposeAsync(previous);
            }
            return next.Token;
        }

        public void Dispose()
        {
            var cts = _cts;
            _cts = null;
            if (cts != null)
            {
                _ = CancellationTokenSourceDisposal.CancelAndDisposeAsync(cts);
            }
        }
    }

    // Debounce search-driven filtering. The SearchText setter (bound two-way to the search box)
    // fires per keystroke, and ApplyFilterAndGrouping does a full off-screen rebuild + collection
    // swap - costly with 700+ repos. Coalesce rapid keystrokes into one filter pass after a 200ms
    // quiet period. The immediate SetSearchText path is left untouched for programmatic/test callers.
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

    public async Task ClearCacheAsync()
    {
        await SetPreparedDiscoveryIndexAsync(null).ConfigureAwait(false);
        GitWizardApi.ClearCache();
        await _dialogs.ShowAlertAsync("Cache Cleared", "Repository cache has been cleared");
    }

    public async Task DeleteAllLocalFilesAsync()
    {
        await SetPreparedDiscoveryIndexAsync(null).ConfigureAwait(false);
        if (!GitWizardApi.TryDeleteAllLocalFiles(out var refusal))
        {
            await _dialogs.ShowAlertAsync("Files Not Deleted", refusal);
            return;
        }
        await _dialogs.ShowAlertAsync("Files Deleted", "All local files have been deleted");
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

        var sorted = RepositorySortOrder.ApplySort(filtered, _activeSortMode);

        // Build off-screen and swap in one shot to avoid per-item layout updates
        var newCollection = new ObservableCollection<RepositoryNodeViewModel>();

        if (_activeGroupMode == GroupMode.None)
        {
            foreach (var repo in sorted)
                newCollection.Add(repo);
        }
        else
        {
            var groups = RepositoryGroupKeys.GroupRepositories(sorted, _activeGroupMode);

            // For remote URL grouping, only show groups with multiple copies (the duplicates you want to clean up)
            var minGroupSize = _activeGroupMode == GroupMode.RemoteUrl ? 2 : 1;

            // Add group headers (collapsed); children are stored on the header node
            foreach (var group in groups.OrderByDescending(g => g.Value.Count))
            {
                if (group.Value.Count < minGroupSize)
                    continue;

                var header = RepositoryNodeViewModel.CreateGroupHeader(group.Key);
                foreach (var repo in RepositorySortOrder.ApplySort(group.Value, _activeSortMode))
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

    // Group-key and rename helpers live in RepositoryGroupKeys.cs.

    internal void ToggleGroupExpand(RepositoryNodeViewModel? node)
    {
        if (node?.IsGroupHeader != true)
        {
            return;
        }

        var index = Repositories.IndexOf(node);
        if (index < 0)
        {
            return;
        }

        if (node.IsExpanded)
        {
            // Collapse: remove children after the header
            node.IsExpanded = false;
            while (index + 1 < Repositories.Count && !Repositories[index + 1].IsGroupHeader)
            {
                Repositories.RemoveAt(index + 1);
            }
        }
        else
        {
            // Expand: insert children after the header
            node.IsExpanded = true;
            var insertIndex = index + 1;
            foreach (var child in node.Children)
            {
                Repositories.Insert(insertIndex++, child);
            }
        }

        node.UpdateDisplayText();
        ScrollToRequest?.Invoke(node);
    }

    void AddToGroups(RepositoryNodeViewModel node)
    {
        var keys = RepositoryGroupKeys.GetGroupKeys(node, _activeGroupMode);
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
                var childIndex = RepositorySortOrder.InsertRepositoryNode(header.Children, node, _activeSortMode);
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

    // Merges one group key's whole batch of new children into its header in one shot, instead of
    // AddToGroups's per-node calls (round-2 pr-crew review of PR #241/#243: see
    // MergeBatchIntoDisplay for why a per-row insert is O(n) per call). Creates the header fresh
    // when none exists yet (the common case for PrePopulateFromReport, which always starts with
    // empty groups/pending state) or bulk-merges into an existing/pending one (the case
    // AddRepositoriesBatch can hit, merging against a header a prior prepopulation or drain
    // already created).
    void MergeGroupBatch(string key, List<RepositoryNodeViewModel> bucket, bool sort = true)
    {
        var minGroupSize = _activeGroupMode == GroupMode.RemoteUrl ? 2 : 1;

        RepositoryNodeViewModel? header = null;
        foreach (var candidate in Repositories)
        {
            if (candidate.IsGroupHeader && candidate.GroupKey == key)
            {
                header = candidate;
                break;
            }
        }

        var wasPending = false;
        if (header == null && _pendingGroups.TryGetValue(key, out var pendingHeader))
        {
            header = pendingHeader;
            wasPending = true;
        }

        if (header != null)
        {
            if (sort)
            {
                RepositorySortOrder.MergeBatchIntoDisplay(header.Children, bucket, _activeSortMode);
            }
            else
            {
                foreach (var node in bucket)
                {
                    header.Children.Add(node);
                }
            }

            header.UpdateDisplayText();
            RefreshExpandedFlatChildren(header);

            if (wasPending && header.Children.Count >= minGroupSize)
            {
                _pendingGroups.Remove(key);
                Repositories.Add(header);
            }

            return;
        }

        header = RepositoryNodeViewModel.CreateGroupHeader(key);
        var nodesToAdd = sort ? RepositorySortOrder.SortBatchForDisplay(bucket, _activeSortMode) : bucket;
        foreach (var node in nodesToAdd)
        {
            header.Children.Add(node);
        }

        header.UpdateDisplayText();

        if (header.Children.Count >= minGroupSize)
        {
            Repositories.Add(header);
        }
        else
        {
            _pendingGroups[key] = header;
        }
    }

    // Rebuilds an expanded header's flat display rows from its own (just-merged) Children,
    // instead of computing per-row insertion points for a whole batch: cheaper than repeated
    // positional inserts, and always exactly consistent with Children's order. A header expanded
    // while a batch merge runs is rare (the common case is a scan streaming into collapsed
    // headers), so a full rebuild of just this header's own flat block is an acceptable cost
    // here - unlike the per-row arrival path, this is not called once per row.
    void RefreshExpandedFlatChildren(RepositoryNodeViewModel header)
    {
        if (!header.IsExpanded)
        {
            return;
        }

        var headerIndex = Repositories.IndexOf(header);
        if (headerIndex < 0)
        {
            return;
        }

        var removeFrom = headerIndex + 1;
        while (removeFrom < Repositories.Count && !Repositories[removeFrom].IsGroupHeader)
        {
            Repositories.RemoveAt(removeFrom);
        }

        var insertAt = removeFrom;
        foreach (var child in header.Children)
        {
            Repositories.Insert(insertAt++, child);
        }
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
