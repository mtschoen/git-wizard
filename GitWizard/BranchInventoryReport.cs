using JetBrains.Annotations;
using LibGit2Sharp;

namespace GitWizard;

/// <summary>
/// Machine-readable branch inventory for automation, safety checks, and branch reaping.
/// </summary>
[Serializable]
public sealed class BranchInventoryReport
{
    public const string CurrentSchemaVersion = "1.1";

    /// <summary>Repositories scanned concurrently by branch inventory and prune (matches refresh).</summary>
    internal static int ScanParallelism => Math.Max(1, Environment.ProcessorCount / 2);

    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string? BaseBranch { get; set; }
    public List<RepositoryBranchItem> Repositories { get; set; } = [];

    /// <summary>
    /// Inspect each repository for local branches and stashes, comparing against the specified
    /// base branch or each repository's default branch (see <see cref="BaseBranchResolver"/>).
    /// <paramref name="localOnly"/> keeps only unmerged branches with commits on no remote-tracking
    /// ref, and only unmerged stashes.
    /// </summary>
    public static BranchInventoryReport Scan(
        IEnumerable<string> repositoryPaths,
        string? baseBranchName = null,
        bool notMergedOnly = false,
        bool localOnly = false)
    {
        ArgumentNullException.ThrowIfNull(repositoryPaths);

        var report = new BranchInventoryReport
        {
            BaseBranch = baseBranchName
        };

        var filter = new ScanFilter(notMergedOnly, localOnly);
        report.Repositories.AddRange(SharedGitDirectories.OnePathPerCommonDirectory(repositoryPaths)
            .AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(ScanParallelism)
            .Select(path => ScanRepository(path, baseBranchName, filter))
            .OfType<RepositoryBranchItem>());

        return report;
    }

    /// <summary>
    /// Which branches and stashes a scan keeps.
    /// </summary>
    readonly record struct ScanFilter(bool NotMergedOnly, bool LocalOnly)
    {
        public bool Keeps(BranchInventoryInfo branch) =>
            !((NotMergedOnly || LocalOnly) && branch.IsMerged) && !(LocalOnly && branch.UniqueCommitCount == 0);

        public bool Keeps(StashInventoryInfo stash) => !(LocalOnly && stash.IsMerged);
    }

    static RepositoryBranchItem? ScanRepository(string path, string? baseBranchName, ScanFilter filter)
    {
        var item = new RepositoryBranchItem { Path = path };

        try
        {
            if (!Repository.IsValid(path))
            {
                return null;
            }

            using var repository = new Repository(path);

            var baseBranch = BaseBranchResolver.Resolve(repository, baseBranchName);
            item.DefaultBranch = baseBranch?.FriendlyName;

            var localBranches = repository.Branches
                .Where(b => !b.IsRemote && b.Tip != null)
                .OrderBy(b => b.FriendlyName, StringComparer.Ordinal)
                .ToList();

            var remoteTips = new List<Commit>();
            foreach (var b in repository.Branches)
            {
                if (b.IsRemote && b.Tip is { } tip)
                {
                    remoteTips.Add(tip);
                }
            }

            foreach (var branch in localBranches)
            {
                var branchInfo = CreateBranchInfo(repository, branch, baseBranch, remoteTips);
                // A local main behind or equal to origin/main is just stale, but unpushed commits on it are real work.
                if (BaseBranchResolver.IsBaseOrCounterpart(branch, baseBranch) && branchInfo.UniqueCommitCount == 0)
                {
                    continue;
                }

                if (filter.Keeps(branchInfo))
                {
                    item.Branches.Add(branchInfo);
                }
            }

            item.Stashes.AddRange(StashMergeDetector.Inventory(repository, baseBranch?.Tip).Where(filter.Keeps));

            if (item.Branches.Count == 0 && item.Stashes.Count == 0)
            {
                return null;
            }

            return item;
        }
        catch (Exception exception)
        {
            item.Error = exception.Message;
            return item;
        }
    }

    static BranchInventoryInfo CreateBranchInfo(
        Repository repository,
        Branch branch,
        Branch? baseBranch,
        List<Commit> remoteTips)
    {
        var aheadBase = 0;
        var behindBase = 0;
        string? mergedBy = null;

        if (baseBranch?.Tip is { } baseTip && branch.Tip is { } branchTip)
        {
            var divergence = repository.ObjectDatabase.CalculateHistoryDivergence(branchTip, baseTip);
            aheadBase = divergence.AheadBy ?? 0;
            behindBase = divergence.BehindBy ?? 0;
            mergedBy = MergeDetector.Detect(repository, branchTip, baseTip);
        }

        var remoteName = branch.RemoteName;
        // URL-valued branch remotes are valid Git config but cannot be resolved by TrackedBranch.
        var hasConfiguredRemote = repository.Network.Remotes.Any(remote =>
            string.Equals(remote.Name, remoteName, StringComparison.Ordinal));
        var trackedBranch = hasConfiguredRemote ? branch.TrackedBranch : null;
        var hasUpstream = trackedBranch != null;
        var upstreamBranch = trackedBranch?.FriendlyName;
        var aheadUpstream = 0;
        var behindUpstream = 0;

        if (trackedBranch?.Tip is { } trackedTip && branch.Tip != null)
        {
            var upDivergence = repository.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, trackedTip);
            aheadUpstream = upDivergence.AheadBy ?? 0;
            behindUpstream = upDivergence.BehindBy ?? 0;
        }

        var uniqueCommitCount = ComputeUniqueCommitCount(repository, branch, baseBranch, remoteTips);

        return new BranchInventoryInfo
        {
            Name = branch.FriendlyName,
            IsMerged = mergedBy != null,
            MergedBy = mergedBy,
            AheadOfBase = aheadBase,
            BehindBase = behindBase,
            HasUpstream = hasUpstream,
            UpstreamBranch = upstreamBranch,
            AheadOfUpstream = aheadUpstream,
            BehindUpstream = behindUpstream,
            UniqueCommitCount = uniqueCommitCount,
            LastCommitDate = branch.Tip?.Author.When
        };
    }

    static int ComputeUniqueCommitCount(
        Repository repository,
        Branch branch,
        Branch? baseBranch,
        List<Commit> remoteTips)
    {
        if (branch.Tip == null)
        {
            return 0;
        }

        var filter = new CommitFilter
        {
            IncludeReachableFrom = branch.Tip
        };

        var excludeTips = new List<Commit>();
        if (baseBranch?.Tip != null)
        {
            excludeTips.Add(baseBranch.Tip);
        }

        foreach (var remoteTip in remoteTips)
        {
            if (!excludeTips.Contains(remoteTip))
            {
                excludeTips.Add(remoteTip);
            }
        }

        if (excludeTips.Count > 0)
        {
            filter.ExcludeReachableFrom = excludeTips;
        }

        return repository.Commits.QueryBy(filter).Count();
    }
}

/// <summary>
/// Branch inventory for a single repository.
/// </summary>
[Serializable]
public sealed class RepositoryBranchItem
{
    public string Path { get; set; } = string.Empty;
    public string? DefaultBranch { get; set; }
    public List<BranchInventoryInfo> Branches { get; set; } = [];
    public List<StashInventoryInfo> Stashes { get; set; } = [];
    [UsedImplicitly]
    public string? Error { get; set; }
}

/// <summary>
/// Information about a single local branch in a repository.
/// </summary>
[Serializable]
public sealed class BranchInventoryInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsMerged { get; set; }
    /// <summary>The <see cref="GitWizard.MergedBy"/> tier that proved the merge, or null when unmerged.</summary>
    public string? MergedBy { get; set; }
    public int AheadOfBase { get; set; }
    public int BehindBase { get; set; }
    public bool HasUpstream { get; set; }
    public string? UpstreamBranch { get; set; }
    public int AheadOfUpstream { get; set; }
    public int BehindUpstream { get; set; }
    public int UniqueCommitCount { get; set; }
    public DateTimeOffset? LastCommitDate { get; set; }
}
