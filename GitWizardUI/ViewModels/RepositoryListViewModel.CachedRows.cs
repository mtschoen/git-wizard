using GitWizard;

namespace GitWizardUI.ViewModels;

public sealed partial class RepositoryListViewModel
{
    HashSet<RepositoryNodeViewModel>? _prePopulatedNodes;

    // Pre-populate the UI from the cached report so repos appear immediately on launch.
    // Existing repos get updated in-place as the refresh completes; new repos discovered
    // during the scan get added normally through OnRepositoryCreated. (Issue #98.)
    internal void PrePopulateFromReport(string[]? repositoryPaths,
        GitWizardReport? cachedReport)
    {
        // These collections describe only the current refresh cycle. Reset them before
        // every early return so paths from a prior refresh cannot suppress fresh rows.
        _prePopulatedNodes = null;

        if (repositoryPaths == null || repositoryPaths.Length == 0 || cachedReport == null || cachedReport.Repositories.Count == 0)
        {
            return;
        }

        var pathsSet = new HashSet<string>(repositoryPaths);

        // Both the ungrouped display batch and each group's batch are collected here and applied
        // once at the end (SortBatchForDisplay / MergeGroupBatch) instead of inserting or calling
        // AddToGroups per row: this loop can process hundreds of cached rows in one synchronous
        // burst, and a per-row insertion costs an O(n) scan-and-shift of the already-inserted,
        // growing list on every iteration - quadratic over the whole batch (pr-crew review of PR
        // #241/#243, rounds 1 and 2). PrePopulateFromReport always runs against empty
        // Repositories/PendingGroups (RefreshAsync clears both immediately before calling it), so
        // MergeGroupBatch's fresh-header path is what actually runs here in practice.
        List<RepositoryNodeViewModel>? ungroupedBatch = null;
        Dictionary<string, List<RepositoryNodeViewModel>>? groupedBatches = null;

        foreach (var (path, repo) in cachedReport.Repositories)
        {
            if (!pathsSet.Contains(path))
            {
                continue;
            }

            var node = new RepositoryNodeViewModel(repo);
            RepositoryMap[path] = node;
            AllRepositories.Add(node);
            _prePopulatedNodes ??= new HashSet<RepositoryNodeViewModel>();
            _prePopulatedNodes.Add(node);

            if (!node.MatchesFilter(ActiveFilter, MainViewModel.GlobalUserEmail))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(SearchText) &&
                !path.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
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
            foreach (var node in RepositorySortOrder.SortBatchForDisplay(ungroupedBatch, ActiveSortMode))
            {
                Repositories.Add(node);
            }
        }

        if (groupedBatches == null)
        {
            return;
        }

        foreach (var (key, bucket) in groupedBatches)
        {
            MergeGroupBatch(key, bucket);
        }
    }

    // After a full refresh, prune repos that were pre-populated but no longer exist
    // in the fresh report (deleted, renamed, or stale non-repo paths).  AllRepositories
    // is cleared at the start of RefreshAsync, so this method only handles cleanup
    // from Repositories and RepositoryMap for nodes whose paths are absent from the
    // fresh scan results (deleted) or that moved (renamed).
    internal void PruneStalePrePopulatedRepos(HashSet<string> freshPaths, HashSet<string> renamedOldPaths)
    {
        if (_prePopulatedNodes == null || _prePopulatedNodes.Count == 0)
        {
            return;
        }

        var stalePaths = _prePopulatedNodes
            .Select(node => node.WorkingDirectory)
            .Where(path => !string.IsNullOrEmpty(path) &&
                           (!freshPaths.Contains(path) || renamedOldPaths.Contains(path)))
            .ToList();

        foreach (var path in stalePaths)
        {
            RemoveRepositoryPathFromUi(path);
        }

        _prePopulatedNodes = null;
    }

    // Remove renamed repos (old path entries) from the UI collections.
    internal void RemoveRenamedReposFromUi(HashSet<string> renamedOldPaths)
    {
        if (renamedOldPaths.Count == 0)
        {
            return;
        }

        foreach (var path in renamedOldPaths)
        {
            RemoveRepositoryPathFromUi(path);
        }
    }

    /// <summary>
    /// Removes a single repository, by working-directory path, from every UI collection
    /// (<see cref="RepositoryMap"/>, <see cref="AllRepositories"/>, <see cref="Repositories"/>, and any
    /// group header holding it). No-op if the path isn't currently tracked. The internal
    /// entry point Live mode's per-event deletion/rename handling in
    /// <c>LiveWatchViewModel.cs</c> calls; the batch-refresh rename cleanup above reaches the
    /// same code directly.
    /// </summary>
    internal void RemoveRepositoryPathFromUi(string path)
    {
        RepositoryMap.TryRemove(path, out _);
        AllRepositories.RemoveAll(candidate => candidate.WorkingDirectory == path);

        var groupHeaders = Repositories.Where(candidate => candidate.IsGroupHeader)
            .Concat(PendingGroups.Values).Distinct().ToList();

        for (var index = Repositories.Count - 1; index >= 0; index--)
        {
            if (Repositories[index].WorkingDirectory == path)
            {
                Repositories.RemoveAt(index);
            }
        }

        foreach (var groupHeader in groupHeaders)
        {
            for (var index = groupHeader.Children.Count - 1; index >= 0; index--)
            {
                if (groupHeader.Children[index].WorkingDirectory == path)
                {
                    groupHeader.Children.RemoveAt(index);
                }
            }

            groupHeader.UpdateDisplayText();
        }
    }
}
