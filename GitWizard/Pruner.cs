using LibGit2Sharp;

namespace GitWizard;

/// <summary>
/// Finds local branches and stashes whose work is already on the base branch and, when asked,
/// deletes them. Dry run by default; every deletion prints a restore command.
/// </summary>
public static class Pruner
{
    /// <summary>
    /// Consider every repository in <paramref name="repositoryPaths"/> once per shared git
    /// directory (linked worktrees share branches and stashes with their main checkout).
    /// Restore commands for deletions are written to <paramref name="restoreHints"/>.
    /// </summary>
    public static PruneReport Run(IEnumerable<string> repositoryPaths, PruneOptions options, TextWriter restoreHints)
    {
        ArgumentNullException.ThrowIfNull(repositoryPaths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(restoreHints);

        var report = new PruneReport { Applied = options.Apply };
        var synchronizedHints = TextWriter.Synchronized(restoreHints);
        // One path per shared git directory also keeps two parallel workers from deleting in
        // the same repository.
        var results = SharedGitDirectories.OnePathPerCommonDirectory(repositoryPaths)
            .AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(BranchInventoryReport.ScanParallelism)
            .Select(path => PruneRepositoryAt(path, options, synchronizedHints))
            .ToList();

        foreach (var (candidates, error) in results)
        {
            report.Candidates.AddRange(candidates);
            if (error != null)
            {
                report.Errors.Add(error);
            }
        }

        return report;
    }

    static (List<PruneCandidate> Candidates, PruneRepositoryError? Error) PruneRepositoryAt(string path, PruneOptions options, TextWriter restoreHints)
    {
        try
        {
            using var repository = new Repository(path);
            return (PruneRepository(repository, path, options, restoreHints), null);
        }
        catch (Exception exception)
        {
            return ([], PruneError(path, exception));
        }
    }

    static PruneRepositoryError PruneError(string path, Exception exception)
    {
        GitWizardLog.LogException(exception, $"Exception pruning {path}");
        return new PruneRepositoryError { Repository = path, Error = exception.Message };
    }

    static List<PruneCandidate> PruneRepository(Repository repository, string path, PruneOptions options, TextWriter restoreHints)
    {
        var baseBranch = BaseBranchResolver.Resolve(repository, options.BaseBranchName);
        var context = new RepositoryContext(repository, path, baseBranch, options.Apply, restoreHints, options.OnlyShas);
        var candidates = new List<PruneCandidate>();
        if (options.Branches)
        {
            candidates.AddRange(PruneBranches(context));
        }

        if (options.Stashes)
        {
            candidates.AddRange(PruneStashes(context));
        }

        return candidates;
    }

    sealed record RepositoryContext(Repository Repository, string Path, Branch? BaseBranch, bool Apply, TextWriter RestoreHints, IReadOnlySet<string>? OnlyShas)
    {
        public bool IsExcluded(string sha) => OnlyShas != null && !OnlyShas.Contains(sha);
    }

    static List<PruneCandidate> PruneBranches(RepositoryContext context)
    {
        var repository = context.Repository;
        var worktreeBranches = WorktreeBranches(repository);
        var headName = repository.Head.Reference is SymbolicReference ? repository.Head.CanonicalName : null;
        var candidates = new List<PruneCandidate>();
        var localBranches = repository.Branches
            .Where(b => b is { IsRemote: false, Tip: not null })
            .OrderBy(b => b.FriendlyName, StringComparer.Ordinal)
            .ToList();

        foreach (var branch in localBranches)
        {
            var excluded = context.IsExcluded(branch.Tip.Sha);
            var mergedBy = !excluded && context.BaseBranch?.Tip is { } baseTip && !BaseBranchResolver.IsBaseOrCounterpart(branch, context.BaseBranch)
                ? MergeDetector.Detect(repository, branch.Tip, baseTip)
                : null;
            var candidate = new PruneCandidate
            {
                Repository = context.Path,
                Kind = PruneCandidate.BranchKind,
                Name = branch.FriendlyName,
                Sha = branch.Tip.Sha,
                MergedBy = mergedBy,
                SkipReason = excluded
                    ? PruneSkipReasons.NotConfirmed
                    : BranchSkipReason(branch, context.BaseBranch, headName, worktreeBranches, mergedBy)
            };
            Decide(candidate, context, () => DeleteBranch(context, candidate));
            candidates.Add(candidate);
        }

        return candidates;
    }

    static string? BranchSkipReason(Branch branch, Branch? baseBranch, string? headName, HashSet<string>? worktreeBranches, string? mergedBy)
    {
        if (BaseBranchResolver.IsBaseOrCounterpart(branch, baseBranch))
        {
            return PruneSkipReasons.BaseBranch;
        }

        if (string.Equals(branch.CanonicalName, headName, StringComparison.Ordinal))
        {
            return PruneSkipReasons.CheckedOut;
        }

        if (worktreeBranches == null)
        {
            return PruneSkipReasons.WorktreeListFailed;
        }

        if (worktreeBranches.Contains(branch.CanonicalName))
        {
            return PruneSkipReasons.CheckedOutInWorktree;
        }

        return mergedBy == null ? PruneSkipReasons.NotMerged : null;
    }

    static List<PruneCandidate> PruneStashes(RepositoryContext context)
    {
        var candidates = StashMergeDetector.Inventory(context.Repository, context.BaseBranch?.Tip)
            .Select(stash => new PruneCandidate
            {
                Repository = context.Path,
                Kind = PruneCandidate.StashKind,
                Name = $"stash@{{{stash.Index}}}",
                Sha = stash.Sha,
                MergedBy = stash.MergedBy,
                Message = stash.Message,
                SkipReason = StashSkipReason(context, stash)
            })
            .ToList();

        // Dropping stash@{n} renumbers every later entry, so drop from the highest index down.
        for (var index = candidates.Count - 1; index >= 0; index--)
        {
            var candidate = candidates[index];
            var stashIndex = index;
            Decide(candidate, context, () => DropStash(context, candidate, stashIndex));
        }

        return candidates;
    }

    static string? StashSkipReason(RepositoryContext context, StashInventoryInfo stash)
    {
        if (context.IsExcluded(stash.Sha))
        {
            return PruneSkipReasons.NotConfirmed;
        }

        return stash.IsMerged ? null : PruneSkipReasons.NotMerged;
    }

    static void Decide(PruneCandidate candidate, RepositoryContext context, Func<string?> delete)
    {
        if (candidate.SkipReason != null)
        {
            candidate.Action = PruneCandidate.Skipped;
            return;
        }

        if (!context.Apply)
        {
            candidate.Action = PruneCandidate.WouldDelete;
            return;
        }

        candidate.SkipReason = delete();
        candidate.Action = candidate.SkipReason == null ? PruneCandidate.Deleted : PruneCandidate.Skipped;
    }

    static string? DeleteBranch(RepositoryContext context, PruneCandidate candidate)
    {
        if (!GitCli.Run(context.Repository, "branch", "-D", candidate.Name).Succeeded)
        {
            return PruneSkipReasons.DeleteFailed;
        }

        context.RestoreHints.WriteLine($"Deleted branch {candidate.Name}; restore with: git -C \"{context.Path}\" branch {candidate.Name} {candidate.Sha}");
        return null;
    }

    static string? DropStash(RepositoryContext context, PruneCandidate candidate, int stashIndex)
    {
        var reference = $"stash@{{{stashIndex}}}";
        var current = GitCli.Run(context.Repository, "rev-parse", "--verify", "--quiet", reference);
        if (!current.Succeeded || !string.Equals(current.Output.Trim(), candidate.Sha, StringComparison.Ordinal))
        {
            GitWizardLog.Log($"{reference} in {context.Path} no longer points at {candidate.Sha}; not dropping it", GitWizardLog.LogType.Warning);
            return PruneSkipReasons.StashMoved;
        }

        if (!GitCli.Run(context.Repository, "stash", "drop", "--quiet", reference).Succeeded)
        {
            return PruneSkipReasons.DeleteFailed;
        }

        var message = candidate.Message?.Replace("\"", "\\\"", StringComparison.Ordinal);
        context.RestoreHints.WriteLine($"Dropped {reference}; restore with: git -C \"{context.Path}\" stash store -m \"{message}\" {candidate.Sha}");
        return null;
    }

    /// <summary>
    /// Canonical refs of branches checked out in any worktree of the repository, or null when git
    /// could not list worktrees (callers then refuse to delete any branch).
    /// </summary>
    static HashSet<string>? WorktreeBranches(Repository repository)
    {
        var result = GitCli.Run(repository, "worktree", "list", "--porcelain");
        if (!result.Succeeded)
        {
            return null;
        }

        const string prefix = "branch ";
        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
    }
}
