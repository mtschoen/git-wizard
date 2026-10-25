using JetBrains.Annotations;
using LibGit2Sharp;

namespace GitWizard;

/// <summary>
/// Machine-readable branch inventory for automation, safety checks, and branch reaping.
/// </summary>
[Serializable]
public sealed class BranchInventoryReport
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string? BaseBranch { get; set; }
    public List<RepositoryBranchItem> Repositories { get; set; } = new();

    /// <summary>
    /// Inspect each repository for local branches, comparing against the specified base branch
    /// or each repository's default branch.
    /// </summary>
    public static BranchInventoryReport Scan(
        IEnumerable<string> repositoryPaths,
        string? baseBranchName = null,
        bool notMergedOnly = false)
    {
        ArgumentNullException.ThrowIfNull(repositoryPaths);

        var report = new BranchInventoryReport
        {
            BaseBranch = baseBranchName
        };

        var sortedPaths = new SortedSet<string>(repositoryPaths, StringComparer.Ordinal);
        foreach (var path in sortedPaths)
        {
            var item = ScanRepository(path, baseBranchName, notMergedOnly);
            if (item != null)
            {
                report.Repositories.Add(item);
            }
        }

        return report;
    }

    static RepositoryBranchItem? ScanRepository(string path, string? baseBranchName, bool notMergedOnly)
    {
        var item = new RepositoryBranchItem { Path = path };

        try
        {
            if (!Repository.IsValid(path))
            {
                return null;
            }

            using var repository = new Repository(path);

            var baseBranch = ResolveBaseBranch(repository, baseBranchName);
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
                // Skip the base branch itself
                if (baseBranch != null && string.Equals(branch.FriendlyName, baseBranch.FriendlyName, StringComparison.Ordinal))
                {
                    continue;
                }

                var branchInfo = CreateBranchInfo(repository, branch, baseBranch, remoteTips, notMergedOnly);
                if (branchInfo != null)
                {
                    item.Branches.Add(branchInfo);
                }
            }

            return item.Branches.Count == 0 ? null : item;
        }
        catch (Exception exception)
        {
            item.Error = exception.Message;
            return item;
        }
    }

    static BranchInventoryInfo? CreateBranchInfo(
        Repository repository,
        Branch branch,
        Branch? baseBranch,
        List<Commit> remoteTips,
        bool notMergedOnly)
    {
        var aheadBase = 0;
        var behindBase = 0;
        var isMerged = false;

        if (baseBranch?.Tip != null && branch.Tip != null)
        {
            var divergence = repository.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, baseBranch.Tip);
            aheadBase = divergence.AheadBy ?? 0;
            behindBase = divergence.BehindBy ?? 0;
            isMerged = aheadBase == 0;
        }

        if (notMergedOnly && isMerged)
        {
            return null;
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
            IsMerged = isMerged,
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

    static Branch? ResolveBaseBranch(Repository repository, string? baseBranchName)
    {
        if (!string.IsNullOrEmpty(baseBranchName))
        {
            var branch = repository.Branches[baseBranchName];
            if (branch != null)
            {
                return branch;
            }

            // Check if there is an origin/<baseBranchName> remote branch
            var remoteBranch = repository.Branches[$"origin/{baseBranchName}"] ?? repository.Branches.FirstOrDefault(b => b.IsRemote && b.FriendlyName.EndsWith($"/{baseBranchName}", StringComparison.OrdinalIgnoreCase));
            if (remoteBranch != null)
            {
                return remoteBranch;
            }
        }

        foreach (var name in new[] { "main", "master", "develop" })
        {
            var candidate = repository.Branches[name];
            if (candidate is { IsRemote: false })
            {
                return candidate;
            }
        }

        return repository.Head.Reference is SymbolicReference ? repository.Head : null;
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
    public List<BranchInventoryInfo> Branches { get; set; } = new();
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
    public int AheadOfBase { get; set; }
    public int BehindBase { get; set; }
    public bool HasUpstream { get; set; }
    public string? UpstreamBranch { get; set; }
    public int AheadOfUpstream { get; set; }
    public int BehindUpstream { get; set; }
    public int UniqueCommitCount { get; set; }
    public DateTimeOffset? LastCommitDate { get; set; }
}
