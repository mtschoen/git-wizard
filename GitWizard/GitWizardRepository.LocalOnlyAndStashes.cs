using LibGit2Sharp;

namespace GitWizard;

// Local-only commit counting and stash inventory, split from GitWizardRepository.cs to keep that file under the size limit.
public partial class GitWizardRepository
{
    /// <summary>
    /// Cached per-branch divergence and cheap-merge results from the most recent refresh,
    /// keyed by (branch tip SHA, comparison tip SHA, base tip SHA). Null for shallow repositories.
    /// </summary>
    public List<BranchDivergenceCacheEntry>? BranchDivergenceCache { get; set; }

    /// <summary>
    /// Number of per-branch divergence and merge-base computations performed during the most
    /// recent <see cref="CollectBranches"/> pass. Exposed to tests to verify cache hits and misses.
    /// </summary>
    internal int BranchDivergenceComputationsForTesting { get; private set; }

    static Dictionary<(string BranchTipSha, string? ComparisonTipSha, string? BaseTipSha), BranchDivergenceCacheEntry>? IndexBranchDivergenceCache(
        Repository repository, List<BranchDivergenceCacheEntry>? entries)
    {
        if (repository.Info.IsShallow)
        {
            return null;
        }

        var indexed = new Dictionary<(string BranchTipSha, string? ComparisonTipSha, string? BaseTipSha), BranchDivergenceCacheEntry>();
        if (entries == null)
        {
            return indexed;
        }

        foreach (var entry in entries)
        {
            indexed[entry.Key] = entry;
        }

        return indexed;
    }

    BranchDivergenceCacheEntry GetOrComputeBranchDivergence(
        Repository repository,
        Commit branchTip,
        Commit? comparisonTip,
        Commit? baseTip,
        Dictionary<(string BranchTipSha, string? ComparisonTipSha, string? BaseTipSha), BranchDivergenceCacheEntry>? cache)
    {
        if (cache == null)
        {
            return ComputeBranchDivergenceEntry(repository, branchTip, comparisonTip, baseTip);
        }

        var key = (branchTip.Sha, comparisonTip?.Sha, baseTip?.Sha);
        if (!cache.TryGetValue(key, out var entry))
        {
            entry = ComputeBranchDivergenceEntry(repository, branchTip, comparisonTip, baseTip);
            cache[key] = entry;
        }

        return entry;
    }

    BranchDivergenceCacheEntry ComputeBranchDivergenceEntry(
        Repository repository, Commit branchTip, Commit? comparisonTip, Commit? baseTip)
    {
        BranchDivergenceComputationsForTesting++;
        var ahead = 0;
        var behind = 0;
        if (comparisonTip != null)
        {
            var divergence = repository.ObjectDatabase.CalculateHistoryDivergence(branchTip, comparisonTip);
            ahead = divergence.AheadBy ?? 0;
            behind = divergence.BehindBy ?? 0;
        }

        var mergedBy = baseTip != null
            ? MergeDetector.DetectCheap(repository, branchTip, baseTip)
            : null;

        return new BranchDivergenceCacheEntry
        {
            BranchTipSha = branchTip.Sha,
            ComparisonTipSha = comparisonTip?.Sha,
            BaseTipSha = baseTip?.Sha,
            Ahead = ahead,
            Behind = behind,
            MergedBy = mergedBy
        };
    }

    /// <summary>
    /// Count commits reachable from any LOCAL branch but not from any
    /// remote-tracking branch - genuinely unpushed commits, counted once
    /// across all local branches. The old per-branch approach counted an
    /// untracked branch's ENTIRE history as unpushed, so already-pushed
    /// mainline commits were multiply-counted and the total could exceed
    /// the repo's commit count. With no remotes, nothing is excluded and
    /// every local commit counts (a brand-new repo is all-local).
    /// </summary>
    /// <param name="repository">The repository to scan.</param>
    internal void RefreshLocalOnlyCommits(Repository repository)
    {
        LocalOnlyCommits = false;
        LocalCommitCount = 0;

        try
        {
            var localTips = repository.Branches
                .Where(branch => !branch.IsRemote)
                .Select(branch => branch.Tip)
                .Where(tip => tip != null)
                .ToList();
            var localTipShas = localTips.Select(tip => tip.Sha).ToHashSet(StringComparer.Ordinal);
            _localOnlyTipShas = new HashSet<string>(StringComparer.Ordinal);

            if (localTips.Count == 0)
            {
                return;
            }

            var filter = new CommitFilter { IncludeReachableFrom = localTips };

            var remoteTips = repository.Branches
                .Where(branch => branch.IsRemote)
                .Select(branch => branch.Tip)
                .Where(tip => tip != null)
                .ToList();
            if (remoteTips.Count > 0)
            {
                filter.ExcludeReachableFrom = remoteTips;
            }

            // A branch has unpushed commits exactly when its tip is one of them (a tip
            // reachable from a remote ref makes its whole history reachable too), so the walk
            // that counts commits also identifies local-only branches at no extra cost.
            foreach (var commit in repository.Commits.QueryBy(filter))
            {
                LocalCommitCount++;
                if (localTipShas.Contains(commit.Sha))
                {
                    _localOnlyTipShas.Add(commit.Sha);
                }
            }

            LocalOnlyCommits = LocalCommitCount > 0;
        }
        catch (Exception exception)
        {
            _localOnlyTipShas = null;
            GitWizardLog.LogException(exception, $"Exception counting local-only commits for {WorkingDirectory}");
        }
    }

    internal void RefreshBranchDivergence(Repository repository, bool allBranches)
    {
        Branch? baseBranch = null;
        try
        {
            baseBranch = BaseBranchResolver.Resolve(repository);
            CollectBranches(repository, allBranches, baseBranch);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception collecting branches for {WorkingDirectory}");
        }

        RefreshStashes(repository, baseBranch?.Tip);
    }

    /// <summary>
    /// Count stashes and those whose changes are all on <paramref name="baseTip"/> (content tier
    /// only). Skipped on a linked worktree: <c>refs/stash</c> is shared with the main checkout,
    /// whose row already counts it.
    /// </summary>
    internal void RefreshStashes(Repository repository, Commit? baseTip)
    {
        StashCount = 0;
        MergedStashCount = 0;
        if (IsWorktree)
        {
            return;
        }

        try
        {
            var stashes = StashMergeDetector.Inventory(repository, baseTip);
            StashCount = stashes.Count;
            MergedStashCount = stashes.Count(stash => stash.IsMerged);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception inventorying stashes for {WorkingDirectory}");
        }
    }
}
