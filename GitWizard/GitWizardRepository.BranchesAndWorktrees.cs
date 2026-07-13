using System.Runtime.InteropServices;
using LibGit2Sharp;

namespace GitWizard;

public partial class GitWizardRepository
{
    void RefreshWorktrees(IUpdateHandler? updateHandler, Repository repository)
    {
        var worktrees = repository.Worktrees;
        if (worktrees.Any())
        {
            Worktrees ??= new SortedDictionary<string, GitWizardRepository?>();
            Parallel.ForEach(worktrees, worktree =>
            {
                try
                {
                    if (worktree == null)
                    {
                        GitWizardLog.Log($"Worktree is null in {repository}");
                        return;
                    }

                    var path = worktree.WorktreeRepository.Info.WorkingDirectory;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        path = path.ToLowerInvariant();

                    bool hasExisting;
                    GitWizardRepository? worktreeRepository;
                    lock (Worktrees)
                    {
                        hasExisting = Worktrees.TryGetValue(path, out worktreeRepository);
                    }

                    if (!hasExisting)
                    {
                        if (Repository.IsValid(path))
                        {
                            worktreeRepository = new GitWizardRepository(path) { IsWorktree = true };
                            lock (Worktrees)
                            {
                                Worktrees[path] = worktreeRepository;
                            }

                            try
                            {
                                updateHandler?.OnWorktreeCreated(worktreeRepository);
                            }
                            catch (Exception exception)
                            {
                                GitWizardLog.LogException(exception,
                                    "Exception thrown by Refresh OnWorktreeCreated callback.");
                            }
                        }
                        else
                        {
                            GitWizardLog.LogException(new InvalidOperationException("Worktree in unknown state"), $"Unknown worktree state for {path}");
                        }
                    }

                    if (worktreeRepository != null)
                    {
                        worktreeRepository.Refresh(updateHandler);
                    }
                }
                catch (Exception exception)
                {
                    GitWizardLog.LogException(exception,
                        $"Exception updating worktrees for {WorkingDirectory}");
                }
            });
        }
    }

    internal void MarkRefreshFailed(string error, IUpdateHandler? updateHandler = null)
    {
        RefreshError = error;
        IsRefreshing = false;

        try
        {
            updateHandler?.OnRepositoryRefreshCompleted(this);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception thrown by MarkRefreshFailed OnRepositoryRefreshCompleted callback.");
        }
    }

    // Returns true when the fetch process ran to completion (regardless of exit code - a non-zero
    // exit can still mean some remotes were fetched successfully), so callers can record a
    // trustworthy LastFetchTime. Returns false when the fetch never completed (failed to start or
    // timed out), so a stale timestamp is left in place rather than overwritten.
    static bool FetchAllRemotes(string workingDirectory)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "fetch --all -q",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                return false;

            if (!process.WaitForExit(60000))
            {
                GitWizardLog.Log($"git fetch --all timed out for {workingDirectory}", GitWizardLog.LogType.Warning);
                process.Kill();
                return false;
            }

            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                if (!string.IsNullOrEmpty(error))
                {
                    GitWizardLog.Log($"git fetch --all reported: {error}", GitWizardLog.LogType.Warning);
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception fetching remotes for {workingDirectory}");
            return false;
        }
    }

    void FindMatchingBranch(Repository repository)
    {
        try
        {
            var detachedTip = repository.Head.Tip;
            if (detachedTip == null)
                return;

            foreach (var branch in repository.Branches)
            {
                if (branch.IsRemote)
                    continue;

                try
                {
                    if (branch.Tip != null && branch.Tip.Sha == detachedTip.Sha)
                    {
                        MatchingBranchName = branch.FriendlyName;
                        return;
                    }
                }
                catch (Exception exception)
                {
                    GitWizardLog.LogException(exception, $"Exception checking branch {branch.FriendlyName} for matching detached HEAD in {WorkingDirectory}");
                }
            }
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception finding matching branch for detached HEAD in {WorkingDirectory}");
        }
    }

    /// <summary>
    /// Resolve the branch to compare others against: main, then master, then
    /// develop (first local that exists), falling back to the current branch
    /// when HEAD is not detached. Returns null only for a detached HEAD with no
    /// main/master/develop.
    /// </summary>
    static Branch? ResolveDefaultBranch(Repository repository)
    {
        foreach (var name in new[] { "main", "master", "develop" })
        {
            var candidate = repository.Branches[name];
            if (candidate is { IsRemote: false })
                return candidate;
        }

        if (repository.Head.Reference is SymbolicReference)
            return repository.Head;

        return null;
    }

    /// <summary>
    /// Populate <see cref="Branches"/> with local branches' divergence from the
    /// default branch. Default population is "actionable" only (drops the
    /// default branch and any branch sitting exactly at its tip); pass
    /// <paramref name="allBranches"/> to emit the full inventory.
    /// </summary>
    void CollectBranches(Repository repository, bool allBranches)
    {
        Branches = null;
        var defaultBranch = ResolveDefaultBranch(repository);
        DefaultBranch = defaultBranch?.FriendlyName;
        if (defaultBranch?.Tip == null)
            return;

        var defaultTip = defaultBranch.Tip;

        var collected = new List<BranchInfo>();
        foreach (var branch in repository.Branches)
        {
            try
            {
                if (branch.IsRemote || branch.Tip == null)
                    continue;

                var divergence = repository.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, defaultTip);
                var ahead = divergence.AheadBy ?? 0;
                var behind = divergence.BehindBy ?? 0;
                var isMerged = ahead == 0;
                var isDefault = branch.FriendlyName == defaultBranch.FriendlyName;

                // "Boring": the default branch itself, or a branch identical to it.
                var boring = isDefault || (ahead == 0 && behind == 0);
                if (!allBranches && boring)
                    continue;

                collected.Add(new BranchInfo
                {
                    Name = branch.FriendlyName,
                    IsMerged = isMerged,
                    MergedInto = isMerged && !isDefault ? defaultBranch.FriendlyName : null,
                    AheadOfDefault = ahead,
                    BehindDefault = behind,
                    LastCommitDate = branch.Tip.Author.When,
                    HasUpstream = branch.TrackedBranch != null,
                });
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, $"Exception collecting branch {branch.FriendlyName} for {WorkingDirectory}");
            }
        }

        Branches = collected.Count > 0 ? collected : null;
    }

    /// <summary>
    /// Compute how far the CURRENTLY CHECKED OUT branch (<c>repository.Head</c>) has diverged from
    /// its upstream remote-tracking branch, and store the result on
    /// <see cref="GitWizardRepository.BehindRemoteCount"/> / <see cref="GitWizardRepository.AheadOfRemoteCount"/>.
    /// This is deliberately independent of <see cref="CollectBranches"/>'s "actionable" branch
    /// filter - the default branch (the branch you are about to publish from) is always dropped
    /// from <c>Branches</c> unless it diverges from ITSELF, so it would otherwise never surface a
    /// behind-remote signal. Left at 0/0 for a detached HEAD or a branch with no upstream (mirrors
    /// <c>HasUpstream</c> on <see cref="BranchInfo"/>: no tracking branch means nothing to compare).
    /// </summary>
    void RefreshRemoteDivergence(Repository repository)
    {
        BehindRemoteCount = 0;
        AheadOfRemoteCount = 0;

        if (IsDetachedHead)
            return;

        try
        {
            var head = repository.Head;
            var trackedTip = head.TrackedBranch?.Tip;
            if (head.Tip == null || trackedTip == null)
                return;

            var divergence = repository.ObjectDatabase.CalculateHistoryDivergence(head.Tip, trackedTip);
            AheadOfRemoteCount = divergence.AheadBy ?? 0;
            BehindRemoteCount = divergence.BehindBy ?? 0;
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception computing remote divergence for {WorkingDirectory}");
        }
    }

    public void CheckoutBranch(string branchName)
    {
        if (string.IsNullOrEmpty(WorkingDirectory))
            throw new InvalidOperationException($"Cannot checkout branch '{branchName}': working directory is not initialized");

        using var repository = new Repository(WorkingDirectory);
        var branch = repository.Branches[branchName];
        Commands.Checkout(repository, branch.Tip);
    }

    static void RefreshIndex(Repository repository)
    {
        try
        {
            // Call git update-index --refresh to update stale stat cache
            // (matches what 'git status' does automatically)
            var workingDirectory = repository.Info.WorkingDirectory;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "update-index --refresh -q",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                if (!process.WaitForExit(30000))
                {
                    GitWizardLog.Log($"git update-index --refresh timed out for {workingDirectory}", GitWizardLog.LogType.Warning);
                    process.Kill();
                    return;
                }

                // Note: git update-index --refresh returns non-zero if files are truly modified,
                // which is normal and expected. We only log if there's an actual error message.
                if (process.ExitCode != 0)
                {
                    var error = process.StandardError.ReadToEnd();
                    if (!string.IsNullOrEmpty(error))
                    {
                        GitWizardLog.Log($"git update-index --refresh reported: {error}", GitWizardLog.LogType.Warning);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception refreshing index via git CLI");
        }
    }

    /// <summary>
    /// Computes the total disk size of all files under <paramref name="path"/> recursively.
    /// </summary>
    /// <remarks>
    /// <para>Callers always pass a validated <see cref="WorkingDirectory"/>, so the path is
    /// guaranteed to be non-null/empty and exist at the time of the call. The check above
    /// the caller is already defensive enough - no need to duplicate it here.</para>
    /// <para>This enumerates ALL files recursively including <c>.git/</c> objects, LFS storage,
    /// build output, etc. For repos with large git object stores the first full scan may
    /// take a long time. The existing <c>RefreshStatus</c> timeout covers this.</para>
    /// </remarks>
    static long ComputeDirectorySize(string path)
    {
        long totalSize = 0;
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                totalSize += file.Length;
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            // Some directories may not be accessible (e.g., $Recycle.Bin, System Volume Information).
            GitWizardLog.Log($"ComputeDirectorySize: skipping inaccessible path: {exception.Message}", GitWizardLog.LogType.Verbose);
        }
        catch (IOException exception)
        {
            // Potential I/O errors on corrupted or networked filesystems; skip and continue.
            GitWizardLog.Log($"ComputeDirectorySize: I/O error, skipping: {exception.Message}", GitWizardLog.LogType.Verbose);
        }
        catch (Exception exception)
        {
            // Catch-all for unexpected exceptions (PathTooLongException, ArgumentException,
            // etc.) - log and return partial result rather than crashing the refresh.
            GitWizardLog.LogException(exception, $"Exception enumerating files for {path}");
        }

        return totalSize;
    }
}
