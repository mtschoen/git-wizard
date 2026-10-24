using LibGit2Sharp;

namespace GitWizard;

/// <summary>
/// One stash entry and whether its changes already exist on the base branch.
/// </summary>
[Serializable]
public sealed class StashInventoryInfo
{
    /// <summary>Position in the stash list (<c>stash@{Index}</c>).</summary>
    public int Index { get; set; }

    /// <summary>The message git stores with the stash (for example "On main: wip").</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>SHA of the stash commit (the working-tree commit <c>refs/stash</c> points at).</summary>
    public string Sha { get; set; } = string.Empty;

    /// <summary>True when <see cref="MergedBy"/> is set.</summary>
    public bool IsMerged { get; set; }

    /// <summary><see cref="GitWizard.MergedBy.Content"/> when every stashed change is on the base, else null.</summary>
    public string? MergedBy { get; set; }
}

/// <summary>
/// Content-based detection for stashes: a stash is merged when every path it changes is
/// byte-identical on the base branch.
/// </summary>
public static class StashMergeDetector
{
    /// <summary>
    /// List every stash in the repository, newest first, marking those whose changes are all on
    /// <paramref name="baseTip"/>. With no base every stash is reported unmerged.
    /// </summary>
    public static List<StashInventoryInfo> Inventory(Repository repository, Commit? baseTip)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var stashes = new List<StashInventoryInfo>();
        var index = 0;
        foreach (var stash in repository.Stashes)
        {
            var mergedBy = baseTip == null ? null : Detect(repository, stash, baseTip);
            stashes.Add(new StashInventoryInfo
            {
                Index = index++,
                Message = stash.Message,
                Sha = stash.WorkTree.Sha,
                IsMerged = mergedBy != null,
                MergedBy = mergedBy
            });
        }

        return stashes;
    }

    /// <summary>
    /// Return <see cref="MergedBy.Content"/> when the stash's working-tree changes, any staged
    /// change that differs from them, and any untracked files all match <paramref name="baseTip"/>;
    /// otherwise null.
    /// </summary>
    public static string? Detect(Repository repository, Stash stash, Commit baseTip)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(stash);
        ArgumentNullException.ThrowIfNull(baseTip);

        var baseTree = baseTip.Tree;
        var workTree = stash.WorkTree.Tree;
        var workTreePaths = MergeDetector.ChangedPaths(repository, stash.Base.Tree, workTree);
        if (!MergeDetector.PathsMatch(workTreePaths, workTree, baseTree))
        {
            return null;
        }

        // A staged change normally ends up in the working-tree commit too. Where the staged
        // version differs from both the stash's base commit and its working tree, it is separate
        // work that must also be on the base.
        var indexTree = stash.Index.Tree;
        var stagedOnlyPaths = MergeDetector.ChangedPaths(repository, stash.Base.Tree, indexTree);
        stagedOnlyPaths.IntersectWith(MergeDetector.ChangedPaths(repository, workTree, indexTree));
        if (!MergeDetector.PathsMatch(stagedOnlyPaths, indexTree, baseTree))
        {
            return null;
        }

        if (stash.Untracked is { } untracked && !MergeDetector.PathsMatch(BlobPaths(untracked.Tree), untracked.Tree, baseTree))
        {
            return null;
        }

        return MergedBy.Content;
    }

    static IEnumerable<string> BlobPaths(Tree tree)
    {
        foreach (var entry in tree)
        {
            if (entry.TargetType == TreeEntryTargetType.Tree)
            {
                foreach (var nested in BlobPaths((Tree)entry.Target))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return entry.Path;
            }
        }
    }
}
