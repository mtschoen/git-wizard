using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    // After a full refresh, prune repos that were pre-populated but no longer exist
    // in the fresh report (deleted, renamed, or stale non-repo paths).  _allRepositories
    // is cleared at the start of RefreshAsync, so this method only handles cleanup
    // from Repositories and _repositoryMap for nodes whose paths are absent from the
    // fresh scan results (deleted) or that moved (renamed).
    internal void PruneStalePrePopulatedRepos(HashSet<string> freshPaths, HashSet<string> renamedOldPaths)
    {
        if (_prePopulatedNodes == null || _prePopulatedNodes.Count == 0)
            return;

        var stalePaths = _prePopulatedNodes
            .Select(node => node.WorkingDirectory)
            .Where(path => !string.IsNullOrEmpty(path) &&
                           (!freshPaths.Contains(path) || renamedOldPaths.Contains(path)))
            .ToList();

        foreach (var path in stalePaths)
            RemoveRepositoryPathFromUi(path);

        _prePopulatedNodes = null;
    }

    // Drop deleted, renamed (old path), and stale non-repo entries from the cached
    // repository-path list.
    internal static async Task UpdateCachedPathsAfterScanAsync(HashSet<string> deletedPaths, HashSet<string> renamedOldPaths,
        HashSet<string> nonRepositoryPaths)
    {
        var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync().ConfigureAwait(false);
        if (cachedPaths == null)
            return;

        var pathsToRemove = deletedPaths.Union(renamedOldPaths).Union(nonRepositoryPaths).ToHashSet();
        if (pathsToRemove.Count > 0)
        {
            var updatedPaths = cachedPaths.Where(p => !pathsToRemove.Contains(p)).ToList();
            await GitWizardApi.SaveCachedRepositoryPathsAsync(updatedPaths).ConfigureAwait(false);
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

    /// <summary>
    /// Removes a single repository, by working-directory path, from every UI collection
    /// (<c>_repositoryMap</c>, <c>_allRepositories</c>, <see cref="Repositories"/>, and any
    /// group header holding it). No-op if the path isn't currently tracked. The internal
    /// entry point Live mode's per-event deletion/rename handling in
    /// <c>MainViewModel.Live.cs</c> calls; the batch-refresh rename cleanup above reaches the
    /// same code directly.
    /// </summary>
    internal void RemoveRepositoryByPath(string path) => RemoveRepositoryPathFromUi(path);

    void RemoveRepositoryPathFromUi(string path)
    {
        _repositoryMap.TryRemove(path, out _);
        _allRepositories.RemoveAll(candidate => candidate.WorkingDirectory == path);

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
