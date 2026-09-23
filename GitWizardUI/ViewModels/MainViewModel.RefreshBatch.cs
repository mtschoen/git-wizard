using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    // Backs AddRepositoriesBatch (MainViewModel.Refresh.cs): the bulk equivalent of calling
    // AddRepository once per repository, for a batch of new-repository arrivals drained together
    // (see DrainUICommands in MainViewModel.Commands.cs, which calls AddRepositoriesBatch for all
    // RepositoryCreated/WorktreeCreated commands in a drained slice instead of routing each one
    // through AddRepository individually). AddRepository's own per-row
    // InsertRepositoryNode/AddToGroups path is fine for one arrival at a time - an isolated
    // Live-watch event, or AddRepositoriesBatch's own single-repository fast path - but doing that
    // once per row for a whole drained batch means shifting up to the whole displayed list on
    // every row: O(n) per row for O(n*k) total data movement across a k-row batch (round-2
    // pr-crew review of PR #241/#243). These two passes instead resolve every repository against
    // _allRepositories/_repositoryMap exactly as AddRepository does (last write wins for a path
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
                continue;

            var node = new RepositoryNodeViewModel(repository);
            _repositoryMap[path] = node;

            var existingNode = _allRepositories.FirstOrDefault(candidate => candidate.WorkingDirectory == path);
            if (existingNode != null)
            {
                var existingIndex = _allRepositories.IndexOf(existingNode);
                _allRepositories[existingIndex] = node;

                if (newNodesByPath.ContainsKey(path))
                    newNodesByPath[path] = node;
                else
                    ReplaceVisibleRepositoryNode(existingNode, node);

                continue;
            }

            _allRepositories.Add(node);
            newNodesByPath[path] = node;
        }

        return newNodesByPath;
    }

    // Pass 2: bucket the surviving new nodes (filter/search-eligible only) into the ungrouped
    // display batch or per-group-key batches, then merge each in one shot.
    void MergeNewNodesIntoDisplay(IEnumerable<RepositoryNodeViewModel> newNodes)
    {
        List<RepositoryNodeViewModel>? ungroupedBatch = null;
        Dictionary<string, List<RepositoryNodeViewModel>>? groupedBatches = null;

        foreach (var node in newNodes)
        {
            if (!node.MatchesFilter(_activeFilter, GlobalUserEmail))
                continue;
            if (!string.IsNullOrWhiteSpace(_searchText) &&
                !node.WorkingDirectory.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
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
            if (IsRefreshing)
            {
                foreach (var node in ungroupedBatch)
                    Repositories.Add(node);
            }
            else
            {
                MergeBatchIntoDisplay(Repositories, ungroupedBatch);
            }
        }

        if (groupedBatches != null)
        {
            foreach (var (key, bucket) in groupedBatches)
                MergeGroupBatch(key, bucket, sort: !IsRefreshing);
        }
    }
}
