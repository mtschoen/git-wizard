using LibGit2Sharp;

namespace GitWizard;

/// <summary>
/// Finds local branches and stashes whose work is already on the base branch and, when asked,
/// deletes eligible branches and drops eligible stashes. Dry run by default; every deletion and
/// every stash archive ref left behind prints a restore command.
/// </summary>
public static partial class Pruner
{
    /// <summary>
    /// Consider every repository in <paramref name="repositoryPaths"/> once per shared git
    /// directory (linked worktrees share branches and stashes with their main checkout).
    /// Restore commands for deleted branches, dropped stashes and archived stashes no longer in
    /// the stash list are written to <paramref name="restoreHints"/>.
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

        foreach (var (candidates, errors) in results)
        {
            report.Candidates.AddRange(candidates);
            report.Errors.AddRange(errors);
        }

        return report;
    }

    static (List<PruneCandidate> Candidates, List<PruneRepositoryError> Errors) PruneRepositoryAt(string path, PruneOptions options, TextWriter restoreHints)
    {
        try
        {
            using var repository = new Repository(path);
            return PruneRepository(repository, path, options, restoreHints);
        }
        catch (Exception exception)
        {
            return ([], [PruneError(path, exception)]);
        }
    }

    static PruneRepositoryError PruneError(string path, Exception exception)
    {
        GitWizardLog.LogException(exception, $"Exception pruning {path}");
        return new PruneRepositoryError { Repository = path, Error = exception.Message };
    }

    static (List<PruneCandidate> Candidates, List<PruneRepositoryError> Errors) PruneRepository(
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
                return ([], [new PruneRepositoryError { Repository = path, Error = message }]);
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

        return (candidates, context.Errors);
    }

    sealed record RepositoryContext(
        Repository Repository,
        string Path,
        Branch? BaseBranch,
        PruneOptions Options,
        TextWriter RestoreHints)
    {
        /// <summary>Failures that leave the repository needing attention, such as a stash that could not be stored back.</summary>
        public List<PruneRepositoryError> Errors { get; } = [];

        public bool IsExcluded(string sha) => Options.OnlyShas != null && !Options.OnlyShas.Contains(sha);

        public void AddError(string message)
        {
            GitWizardLog.Log($"{message} ({Path})", GitWizardLog.LogType.Warning);
            Errors.Add(new PruneRepositoryError { Repository = Path, Error = message });
        }
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
