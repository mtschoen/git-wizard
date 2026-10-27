using LibGit2Sharp;

namespace GitWizard;

/// <summary>
/// Finds local branches and stashes whose work is already on the base branch and, when asked,
/// deletes eligible branches. Dry run by default; every branch deletion prints a restore command,
/// and every merged stash prints a manual verify-and-drop command.
/// </summary>
public static class Pruner
{
    /// <summary>
    /// Consider every repository in <paramref name="repositoryPaths"/> once per shared git
    /// directory (linked worktrees share branches and stashes with their main checkout).
    /// Restore commands for deleted branches and manual drop commands for merged stashes are
    /// written to <paramref name="restoreHints"/>.
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
            return PruneRepository(repository, path, options, restoreHints);
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

    static (List<PruneCandidate> Candidates, PruneRepositoryError? Error) PruneRepository(
        Repository repository,
        string path,
        PruneOptions options,
        TextWriter restoreHints)
    {
        Branch? baseBranch;
        if (!string.IsNullOrEmpty(options.BaseBranchName))
        {
            baseBranch = BaseBranchResolver.ResolveExplicit(repository, options.BaseBranchName);
            if (baseBranch?.Tip == null)
            {
                var message = $"Base branch '{options.BaseBranchName}' was not found.";
                GitWizardLog.Log($"{message} ({path})", GitWizardLog.LogType.Warning);
                return ([], new PruneRepositoryError { Repository = path, Error = message });
            }
        }
        else
        {
            baseBranch = BaseBranchResolver.Resolve(repository);
        }

        var context = new RepositoryContext(repository, path, baseBranch, options, restoreHints);
        var candidates = new List<PruneCandidate>();
        if (options.Branches)
        {
            candidates.AddRange(PruneBranches(context));
        }

        if (options.Stashes)
        {
            candidates.AddRange(PruneStashes(context));
        }

        return (candidates, null);
    }

    sealed record RepositoryContext(
        Repository Repository,
        string Path,
        Branch? BaseBranch,
        PruneOptions Options,
        TextWriter RestoreHints)
    {
        public bool IsExcluded(string sha) => Options.OnlyShas != null && !Options.OnlyShas.Contains(sha);
    }

    static List<PruneCandidate> PruneBranches(RepositoryContext context)
    {
        var repository = context.Repository;
        var worktreeBranches = WorktreeBranches(repository);
        var headName = CurrentHeadCanonicalName(repository);
        var candidates = new List<PruneCandidate>();
        var localBranches = repository.Branches
            .Where(branch => !branch.IsRemote && (branch.Tip != null || branch.Reference is SymbolicReference))
            .OrderBy(branch => branch.FriendlyName, StringComparer.Ordinal)
            .ToList();

        foreach (var branch in localBranches)
        {
            var isSymbolicRef = branch.Reference is SymbolicReference;
            var tipSha = branch.Tip?.Sha ?? string.Empty;
            var excluded = !isSymbolicRef && context.IsExcluded(tipSha);
            var mergedBy = !isSymbolicRef && !excluded && branch.Tip is { } branchTip && context.BaseBranch?.Tip is { } baseTip && !BaseBranchResolver.IsBaseOrCounterpart(branch, context.BaseBranch)
                ? MergeDetector.Detect(repository, branchTip, baseTip)
                : null;
            var candidate = new PruneCandidate
            {
                Repository = context.Path,
                Kind = PruneCandidate.BranchKind,
                Name = branch.FriendlyName,
                Sha = tipSha,
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

    static string? CurrentHeadCanonicalName(Repository repository) =>
        repository.Head.Reference is SymbolicReference ? repository.Head.CanonicalName : null;

    static string? BranchSkipReason(Branch branch, Branch? baseBranch, string? headName, HashSet<string>? worktreeBranches, string? mergedBy)
    {
        if (branch.Reference is SymbolicReference)
        {
            return PruneSkipReasons.SymbolicRef;
        }

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

        var quotedPath = ShellQuote(context.Path);
        foreach (var candidate in candidates)
        {
            if (candidate.SkipReason != null)
            {
                candidate.Action = PruneCandidate.Skipped;
                continue;
            }

            candidate.Action = PruneCandidate.ManualDrop;
            context.RestoreHints.WriteLine(
                $"Merged {candidate.Name} ({candidate.Sha}); check with: git -C {quotedPath} stash list --format='%H %gd' | grep '^{candidate.Sha} ' then drop with: git -C {quotedPath} stash drop {candidate.Name}");
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

        if (!context.Options.Apply)
        {
            candidate.Action = PruneCandidate.WouldDelete;
            return;
        }

        context.Options.BeforeDelete?.Invoke(candidate);
        candidate.SkipReason = delete();
        candidate.Action = candidate.SkipReason == null ? PruneCandidate.Deleted : PruneCandidate.Skipped;
    }

    static string? DeleteBranch(RepositoryContext context, PruneCandidate candidate)
    {
        var canonicalName = $"refs/heads/{candidate.Name}";
        var headName = CurrentHeadCanonicalName(context.Repository);
        var worktreeBranches = WorktreeBranches(context.Repository);
        if (string.Equals(canonicalName, headName, StringComparison.Ordinal)
            || worktreeBranches?.Contains(canonicalName) == true)
        {
            return PruneSkipReasons.CheckedOut;
        }

        if (worktreeBranches == null)
        {
            return PruneSkipReasons.WorktreeListFailed;
        }

        if (!GitCli.Run(context.Repository, "update-ref", "--no-deref", "-d", canonicalName, candidate.Sha).Succeeded)
        {
            return PruneSkipReasons.DeleteFailed;
        }

        GitCli.RunQuiet(context.Repository, "config", "--local", "--remove-section", $"branch.{candidate.Name}");
        context.RestoreHints.WriteLine($"Deleted branch {candidate.Name}; restore with: git -C {ShellQuote(context.Path)} branch {ShellQuote(candidate.Name)} {candidate.Sha}");
        return null;
    }

    // Restore hints are meant to be pasted into a POSIX shell; branch names and paths
    // may contain $, backticks or quotes, so each is single-quoted.
    internal static string ShellQuote(string? value) =>
        "'" + (value ?? string.Empty).Replace("'", "'\\''", StringComparison.Ordinal) + "'";

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
