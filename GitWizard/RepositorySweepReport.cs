using LibGit2Sharp;

namespace GitWizard;

/// <summary>
/// Compact repository-safety inventory for automation and fleet sweeps.
/// </summary>
[Serializable]
public sealed class RepositorySweepReport
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<RepositorySweepItem> Repositories { get; set; } = new();

    /// <summary>
    /// Inspect each repository independently, preserving failures as data so one unreadable
    /// checkout cannot invalidate the complete machine sweep.
    /// </summary>
    public static RepositorySweepReport Scan(IEnumerable<string> repositoryPaths)
    {
        ArgumentNullException.ThrowIfNull(repositoryPaths);

        var report = new RepositorySweepReport();
        foreach (var path in new SortedSet<string>(repositoryPaths, StringComparer.Ordinal))
        {
            report.Repositories.Add(ScanRepository(path));
        }

        return report;
    }

    static RepositorySweepItem ScanRepository(string path)
    {
        var item = new RepositorySweepItem { Path = path };
        try
        {
            using var repository = new Repository(path);
            item.DirtyTrackedFiles = CollectDirtyTrackedFiles(repository);
            item.UnpushedBranches = CollectUnpushedBranches(repository);
            item.StashCount = repository.Stashes.Count();
        }
        catch (Exception exception)
        {
            item.Error = exception.Message;
        }

        return item;
    }

    static List<string> CollectDirtyTrackedFiles(Repository repository)
    {
        const FileStatus trackedChanges =
            FileStatus.NewInIndex |
            FileStatus.ModifiedInIndex |
            FileStatus.DeletedFromIndex |
            FileStatus.RenamedInIndex |
            FileStatus.TypeChangeInIndex |
            FileStatus.ModifiedInWorkdir |
            FileStatus.DeletedFromWorkdir |
            FileStatus.RenamedInWorkdir |
            FileStatus.TypeChangeInWorkdir |
            FileStatus.Unreadable |
            FileStatus.Conflicted;

        return repository.RetrieveStatus()
            .Where(entry => (entry.State & trackedChanges) != 0)
            .Select(entry => entry.FilePath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    static List<UnpushedBranchInfo> CollectUnpushedBranches(Repository repository)
    {
        var remoteTips = repository.Branches
            .Where(branch => branch.IsRemote && branch.Tip != null)
            .Select(branch => branch.Tip)
            .ToList();

        var branches = new List<UnpushedBranchInfo>();
        foreach (var branch in repository.Branches.Where(branch => !branch.IsRemote && branch.Tip != null))
        {
            var filter = new CommitFilter { IncludeReachableFrom = branch.Tip };
            if (remoteTips.Count > 0)
                filter.ExcludeReachableFrom = remoteTips;

            var commitCount = repository.Commits.QueryBy(filter).Count();
            if (commitCount > 0)
            {
                branches.Add(new UnpushedBranchInfo
                {
                    Name = branch.FriendlyName,
                    CommitCount = commitCount
                });
            }
        }

        return branches.OrderBy(branch => branch.Name, StringComparer.Ordinal).ToList();
    }
}

[Serializable]
public sealed class RepositorySweepItem
{
    public string Path { get; set; } = string.Empty;
    public List<string> DirtyTrackedFiles { get; set; } = new();
    public List<UnpushedBranchInfo> UnpushedBranches { get; set; } = new();
    public int StashCount { get; set; }
    public string? Error { get; set; }
}

[Serializable]
public sealed class UnpushedBranchInfo
{
    public string Name { get; set; } = string.Empty;
    public int CommitCount { get; set; }
}
