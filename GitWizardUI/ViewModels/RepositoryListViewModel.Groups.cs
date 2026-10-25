namespace GitWizardUI.ViewModels;

public sealed partial class RepositoryListViewModel
{
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
                PendingGroups.TryGetValue(key, out header);
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
                    PendingGroups[key] = header;
                }
            }
        }

        // Check if any pending groups now meet the minimum size
        if (PendingGroups.Count > 0)
        {
            var promoted = new List<string>();
            foreach (var kvp in PendingGroups)
            {
                if (kvp.Value.Children.Count >= minGroupSize)
                {
                    kvp.Value.UpdateDisplayText();
                    Repositories.Add(kvp.Value);
                    promoted.Add(kvp.Key);
                }
            }

            foreach (var key in promoted)
            {
                PendingGroups.Remove(key);
            }
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
        if (header == null && PendingGroups.TryGetValue(key, out var pendingHeader))
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
                PendingGroups.Remove(key);
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
            PendingGroups[key] = header;
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
}
