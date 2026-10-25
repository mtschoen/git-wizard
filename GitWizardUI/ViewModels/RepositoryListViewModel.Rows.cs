using GitWizard;

namespace GitWizardUI.ViewModels;

public sealed partial class RepositoryListViewModel
{
    internal void AddRepository(GitWizardRepository repository, bool streaming)
    {
        var path = repository.WorkingDirectory;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var node = new RepositoryNodeViewModel(repository);
        RepositoryMap[path] = node;

        var existingNode = AllRepositories.FirstOrDefault(candidate => candidate.WorkingDirectory == path);
        if (existingNode != null)
        {
            var existingIndex = AllRepositories.IndexOf(existingNode);
            AllRepositories[existingIndex] = node;
            ReplaceVisibleRepositoryNode(existingNode, node);
            return;
        }

        AllRepositories.Add(node);

        if (!node.MatchesFilter(ActiveFilter, MainViewModel.GlobalUserEmail))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !node.WorkingDirectory.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (ActiveGroupMode == GroupMode.None)
        {
            if (Repositories.Any(candidate => candidate.WorkingDirectory == path))
            {
                return;
            }

            if (streaming)
            {
                Repositories.Add(node);
            }
            else
            {
                RepositorySortOrder.InsertRepositoryNode(Repositories, node, ActiveSortMode);
            }
        }
        else
        {
            if (streaming)
            {
                foreach (var key in RepositoryGroupKeys.GetGroupKeys(node, ActiveGroupMode))
                {
                    MergeGroupBatch(key, new List<RepositoryNodeViewModel> { node }, sort: false);
                }
            }
            else
            {
                AddToGroups(node);
            }
        }
    }

    // Bulk equivalent of AddRepository for a drained batch of arrivals; see
    // ResolveBatchAgainstExistingRepositories and MergeNewNodesIntoDisplay below for why and how
    // (round-2 pr-crew review of PR #241/#243).
    internal void AddRepositoriesBatch(IReadOnlyList<GitWizardRepository> repositories, bool streaming)
    {
        switch (repositories.Count)
        {
            case 0:
                return;
            case 1 when !streaming:
                AddRepository(repositories[0], streaming: false);
                return;
        }

        var newNodesByPath = ResolveBatchAgainstExistingRepositories(repositories);
        if (newNodesByPath.Count > 0)
        {
            MergeNewNodesIntoDisplay(newNodesByPath.Values, streaming);
        }
    }

    internal void ClearRepositories()
    {
        Repositories.Clear();
        PendingGroups.Clear();
        AllRepositories.Clear();
        RepositoryMap.Clear();
    }

    void ReplaceVisibleRepositoryNode(RepositoryNodeViewModel existingNode, RepositoryNodeViewModel freshNode)
    {
        var visibleIndex = Repositories.IndexOf(existingNode);
        if (visibleIndex >= 0)
        {
            Repositories[visibleIndex] = freshNode;
        }

        foreach (var groupHeader in Repositories.Where(candidate => candidate.IsGroupHeader)
                     .Concat(PendingGroups.Values).Distinct())
        {
            var childIndex = groupHeader.Children.IndexOf(existingNode);
            if (childIndex < 0)
            {
                continue;
            }

            groupHeader.Children[childIndex] = freshNode;
            groupHeader.UpdateDisplayText();
        }
    }

    internal void AddSubmodule(GitWizardRepository parent, GitWizardRepository submodule)
    {
        var parentPath = parent.WorkingDirectory;
        if (string.IsNullOrEmpty(parentPath) || !RepositoryMap.TryGetValue(parentPath, out var parentNode))
        {
            return;
        }

        var submodulePath = submodule.WorkingDirectory;
        if (string.IsNullOrEmpty(submodulePath))
        {
            return;
        }

        var submoduleNode = new RepositoryNodeViewModel(submodule);
        RepositoryMap[submodulePath] = submoduleNode;
        parentNode.Children.Add(submoduleNode);
    }

    internal static void AddUninitializedSubmodule(GitWizardRepository parent, string submodulePath)
    {
        // Uninitialized submodules are intentionally not shown in the tree view yet; they could be
        // added as a special node type in the future. Record the skip for verbose diagnostics.
        GitWizardLog.Log($"Skipping uninitialized submodule {submodulePath} under {parent.WorkingDirectory}",
            GitWizardLog.LogType.Verbose);
    }

    internal void UpdateCompletedRepository(GitWizardRepository repository, bool streaming)
    {
        var path = repository.WorkingDirectory;

        if (!string.IsNullOrEmpty(repository.RefreshError))
        {
            _errorLog.PostError(_ui, repository.RefreshError, path);
        }

        if (string.IsNullOrEmpty(path) || !RepositoryMap.TryGetValue(path, out var node))
        {
            return;
        }

        node.Update();

        // For filtering (no grouping): incrementally add/remove without rebuilding
        if (ActiveFilter != FilterType.None && ActiveGroupMode == GroupMode.None)
        {
            var isShown = Repositories.Contains(node);
            var shouldShow = node.MatchesFilter(ActiveFilter, MainViewModel.GlobalUserEmail);
            switch (isShown, shouldShow)
            {
                case (true, false):
                    Repositories.Remove(node);
                    break;
                case (false, true):
                    if (streaming)
                    {
                        Repositories.Add(node);
                    }
                    else
                    {
                        RepositorySortOrder.InsertRepositoryNode(Repositories, node, ActiveSortMode);
                    }

                    break;
            }

            UpdateHeaderWithFilterInfo();
        }
        // A repository can appear in multiple remote groups; refresh every aggregate and status summary.
        if (ActiveGroupMode == GroupMode.None)
        {
            return;
        }

        foreach (var header in node.ParentHeaders)
        {
            header.UpdateDisplayText();
        }
    }

    // Backs AddRepositoriesBatch above: the bulk equivalent of calling
    // AddRepository once per repository, for a batch of new-repository arrivals drained together
    // (see DrainUICommands in RefreshCoordinator.UiDrain.cs, which calls AddRepositoriesBatch for all
    // RepositoryCreated/WorktreeCreated commands in a drained slice instead of routing each one
    // through AddRepository individually). AddRepository's own per-row
    // InsertRepositoryNode/AddToGroups path is fine for one arrival at a time - an isolated
    // Live-watch event, or AddRepositoriesBatch's own single-repository fast path - but doing that
    // once per row for a whole drained batch means shifting up to the whole displayed list on
    // every row: O(n) per row for O(n*k) total data movement across a k-row batch (round-2
    // pr-crew review of PR #241/#243). These two passes instead resolve every repository against
    // AllRepositories/RepositoryMap exactly as AddRepository does (last write wins for a path
    // repeated within this one batch - ResolveBatchAgainstExistingRepositories), then merge the
    // surviving new rows into the display in one shot per affected collection
    // (MergeNewNodesIntoDisplay -> MergeBatchIntoDisplay / MergeGroupBatch).

    // Pass 1: track only paths that need a brand-new display row. A path repeated within this one
    // batch keeps whichever node arrived last, without giving the earlier one a row: it was never
    // made visible, so there is nothing to replace yet (ReplaceVisibleRepositoryNode would be a
    // no-op for it). A path that already had a visible row from an earlier cycle/batch is replaced
    // in place immediately, same as AddRepository.
    Dictionary<string, RepositoryNodeViewModel> ResolveBatchAgainstExistingRepositories(
        IReadOnlyList<GitWizardRepository> repositories)
    {
        var newNodesByPath = new Dictionary<string, RepositoryNodeViewModel>();
        foreach (var repository in repositories)
        {
            var path = repository.WorkingDirectory;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var node = new RepositoryNodeViewModel(repository);
            RepositoryMap[path] = node;

            var existingNode = AllRepositories.FirstOrDefault(candidate => candidate.WorkingDirectory == path);
            if (existingNode != null)
            {
                var existingIndex = AllRepositories.IndexOf(existingNode);
                AllRepositories[existingIndex] = node;

                if (newNodesByPath.ContainsKey(path))
                {
                    newNodesByPath[path] = node;
                }
                else
                {
                    ReplaceVisibleRepositoryNode(existingNode, node);
                }

                continue;
            }

            AllRepositories.Add(node);
            newNodesByPath[path] = node;
        }

        return newNodesByPath;
    }

    // Pass 2: bucket the surviving new nodes (filter/search-eligible only) into the ungrouped
    // display batch or per-group-key batches, then merge each in one shot.
    void MergeNewNodesIntoDisplay(IEnumerable<RepositoryNodeViewModel> newNodes, bool streaming)
    {
        List<RepositoryNodeViewModel>? ungroupedBatch = null;
        Dictionary<string, List<RepositoryNodeViewModel>>? groupedBatches = null;

        foreach (var node in newNodes)
        {
            if (!node.MatchesFilter(ActiveFilter, MainViewModel.GlobalUserEmail))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(SearchText) &&
                !node.WorkingDirectory.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ActiveGroupMode == GroupMode.None)
            {
                ungroupedBatch ??= new List<RepositoryNodeViewModel>();
                ungroupedBatch.Add(node);
            }
            else
            {
                foreach (var key in RepositoryGroupKeys.GetGroupKeys(node, ActiveGroupMode))
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
            if (streaming)
            {
                foreach (var node in ungroupedBatch)
                {
                    Repositories.Add(node);
                }
            }
            else
            {
                RepositorySortOrder.MergeBatchIntoDisplay(Repositories, ungroupedBatch, ActiveSortMode);
            }
        }

        if (groupedBatches == null)
        {
            return;
        }

        foreach (var (key, bucket) in groupedBatches)
        {
            MergeGroupBatch(key, bucket, sort: !streaming);
        }
    }
}
