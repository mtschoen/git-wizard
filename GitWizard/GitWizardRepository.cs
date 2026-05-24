using LibGit2Sharp;
using System;
using System.Runtime.InteropServices;

namespace GitWizard;

[Serializable]
public class GitWizardRepository
{
    public string? WorkingDirectory { get; private set; }
    public string? CurrentBranch { get; private set; }
    public bool IsDetachedHead { get; private set; }
    public bool HasPendingChanges { get; private set; }
    public int NumberOfPendingChanges { get; private set; }
    public bool IsWorktree { get; private set; }
    public SortedDictionary<string, GitWizardRepository?>? Submodules { get; private set; }
    public SortedDictionary<string, GitWizardRepository?>? Worktrees { get; private set; }

    public bool IsRefreshing { get; private set; }
    public bool LocalOnlyCommits { get; private set; }
    public int LocalCommitCount { get; private set; }
    public List<string> RemoteUrls { get; private set; } = new();
    public DateTimeOffset? LastCommitDate { get; private set; }
    public double RefreshTimeSeconds { get; set; }
    public string? RefreshError { get; set; }
    public HashSet<string>? AuthorEmails { get; private set; }
    public List<GitWizardCommitInfo>? RecentCommits { get; private set; }
    public int? DaysSinceLastCommit { get; private set; }
    public string? MatchingBranchName { get; private set; }
    public string? DefaultBranch { get; private set; }
    public List<BranchInfo>? Branches { get; set; }
    public long SizeOnDisk { get; private set; }

    GitWizardRepository() { }

    public GitWizardRepository(string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
    }

    public void Refresh(IUpdateHandler? updateHandler = null, bool fetchRemotes = false,
        bool deepRefresh = false, bool allBranches = false)
    {
        if (string.IsNullOrEmpty(WorkingDirectory) || !Directory.Exists(WorkingDirectory))
        {
            GitWizardLog.Log($"Working directory {WorkingDirectory} is invalid.", GitWizardLog.LogType.Error);
            return;
        }

        if (fetchRemotes)
            FetchAllRemotes(WorkingDirectory);

        GitWizardLog.Log($"Refreshing {WorkingDirectory}");

        try
        {
            var repository = new Repository(WorkingDirectory);
            IsRefreshing = true;
            RefreshSubmodules(updateHandler, repository, WorkingDirectory);

            // Worktrees can't have worktrees, and trying to refresh them will cause an infinite loop
            if (!IsWorktree)
                RefreshWorktrees(updateHandler, repository);

            CurrentBranch = repository.Head.FriendlyName;
            IsDetachedHead = repository.Head.Reference is not SymbolicReference;
            if (IsDetachedHead)
                FindMatchingBranch(repository);
            LastCommitDate = repository.Head.Tip?.Author.When;
            if (LastCommitDate.HasValue)
                DaysSinceLastCommit = (int)(DateTimeOffset.Now - LastCommitDate.Value).TotalDays;

            try
            {
                // Refresh the index to update stale stat data (slow on large repos / slow drives)
                // Only run during deep refresh to keep auto-refresh fast
                if (deepRefresh)
                    RefreshIndex(repository);

                var status = repository.RetrieveStatus();
                HasPendingChanges = status.IsDirty;
                if (HasPendingChanges)
                {
                    // Must match the categories that contribute to status.IsDirty so
                    // callers never see HasPendingChanges=true with a count of 0.
                    // LibGit2Sharp's IsDirty includes Modified, Staged, Removed, Added,
                    // Untracked, RenamedInIndex, and RenamedInWorkDir.
                    NumberOfPendingChanges = 0;
                    foreach (var _ in status.Modified) NumberOfPendingChanges++;
                    foreach (var _ in status.Staged) NumberOfPendingChanges++;
                    foreach (var _ in status.Removed) NumberOfPendingChanges++;
                    foreach (var _ in status.Added) NumberOfPendingChanges++;
                    foreach (var _ in status.Untracked) NumberOfPendingChanges++;
                    foreach (var _ in status.RenamedInIndex) NumberOfPendingChanges++;
                    foreach (var _ in status.RenamedInWorkDir) NumberOfPendingChanges++;
                }
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, $"Exception retrieving status for {WorkingDirectory}");
            }

            // Collect remote URLs
            RemoteUrls.Clear();
            foreach (var remote in repository.Network.Remotes)
            {
                if (!string.IsNullOrEmpty(remote.Url))
                    RemoteUrls.Add(remote.Url);
            }

            // Reset before re-checking
            LocalOnlyCommits = false;
            LocalCommitCount = 0;

            // TODO: Enable/disable deep checks
            foreach (var branch in repository.Branches)
            {
                // Check if this is a local branch (not a remote tracking branch)
                if (branch.IsRemote)
                    continue;

                // Case 1: Local branch not tracking any remote
                if (branch.TrackedBranch == null)
                {
                    LocalOnlyCommits = true;
                    try
                    {
                        // Every commit on this branch is effectively unpushed.
                        LocalCommitCount += branch.Commits.Count();
                    }
                    catch (Exception exception)
                    {
                        GitWizardLog.LogException(exception, $"Exception counting commits on untracked branch {branch.FriendlyName} for {WorkingDirectory}");
                    }
                    continue;
                }

                // Case 2: Local branch is ahead of its remote tracking branch
                if (branch.Tip != null && branch.TrackedBranch.Tip != null && branch.Tip != branch.TrackedBranch.Tip)
                {
                    var divergence = repository.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, branch.TrackedBranch.Tip);
                    if (divergence.AheadBy > 0)
                    {
                        LocalOnlyCommits = true;
                        LocalCommitCount += divergence.AheadBy ?? 0;
                    }
                }
            }

            // Collect per-branch divergence from the default branch.
            try
            {
                CollectBranches(repository, allBranches);
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, $"Exception collecting branches for {WorkingDirectory}");
            }

            // Cache author emails for "My Repositories" filter
            try
            {
                AuthorEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var commit in repository.Commits.Take(200))
                {
                    AuthorEmails.Add(commit.Author.Email);
                }
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, $"Exception collecting author emails for {WorkingDirectory}");
            }

            // Collect recent commits for projdash/LLM consumption
            try
            {
                RecentCommits = new List<GitWizardCommitInfo>();
                foreach (var commit in repository.Commits.Take(10))
                {
                    RecentCommits.Add(new GitWizardCommitInfo
                    {
                        Hash = commit.Sha[..7],
                        Message = commit.MessageShort,
                        Date = commit.Author.When,
                        AuthorEmail = commit.Author.Email
                    });
                }
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, $"Exception collecting recent commits for {WorkingDirectory}");
            }

            // Compute size on disk
            try
            {
                SizeOnDisk = ComputeDirectorySize(WorkingDirectory);
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, $"Exception computing size for {WorkingDirectory}");
            }

            IsRefreshing = false;

            try
            {
                updateHandler?.OnRepositoryRefreshCompleted(this);
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, "Exception thrown by Refresh OnRepositoryRefreshCompleted callback.");
            }
        }
        catch (Exception exception)
        {
            RefreshError = exception.Message;
            GitWizardLog.LogException(exception, $"Exception thrown trying to refresh {WorkingDirectory}");
            IsRefreshing = false;

            try
            {
                updateHandler?.OnRepositoryRefreshCompleted(this);
            }
            catch (Exception callbackException)
            {
                GitWizardLog.LogException(callbackException, "Exception thrown by Refresh OnRepositoryRefreshCompleted callback.");
            }
        }
    }

    void RefreshSubmodules(IUpdateHandler? updateHandler, Repository repository, string workingDirectory)
    {
        try
        {
            var submodules = repository.Submodules;
            if (!submodules.Any())
                return;

            Submodules ??= new SortedDictionary<string, GitWizardRepository?>();
            Parallel.ForEach(submodules, submodule =>
            {
                var path = Path.Combine(workingDirectory, submodule.Path);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    path = path.ToLowerInvariant();

                GitWizardRepository? submoduleRepository;
                bool hasExisting;
                lock (Submodules)
                {
                    hasExisting = Submodules.TryGetValue(path, out submoduleRepository);
                }

                if (!hasExisting)
                {
                    if (submodule.WorkDirCommitId == null)
                    {
                        // Uninitialized submodules will have a null work directory commit id
                        lock (Submodules)
                        {
                            Submodules[path] = null;
                        }

                        try
                        {
                            updateHandler?.OnUninitializedSubmoduleCreated(this, path);
                        }
                        catch (Exception exception)
                        {
                            GitWizardLog.LogException(exception,
                                "Exception thrown by Refresh OnUninitializedSubmoduleCreated callback.");
                        }
                    }
                    else
                    {
                        try
                        {
                            if (Repository.IsValid(path))
                            {
                                submoduleRepository = new GitWizardRepository(path);
                                lock (Submodules)
                                {
                                    Submodules[path] = submoduleRepository;
                                }

                                try
                                {
                                    updateHandler?.OnSubmoduleCreated(this, submoduleRepository);
                                }
                                catch (Exception exception)
                                {
                                    GitWizardLog.LogException(exception,
                                        "Exception thrown by Refresh OnSubmoduleCreated callback.");
                                }
                            }
                            else
                            {
                                GitWizardLog.LogException(new Exception(), $"Unknown submodule state for {path}");
                            }
                        }
                        catch (Exception exception)
                        {
                            GitWizardLog.LogException(exception,
                                $"Exception updating submodules for {WorkingDirectory}");
                        }
                    }
                }

                if (submoduleRepository == null)
                    return;

                submoduleRepository.Refresh(updateHandler);
            });
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception enumerating submodules for {WorkingDirectory}");
        }
    }

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
                            worktreeRepository = new GitWizardRepository(path) {IsWorktree = true};
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
                            GitWizardLog.LogException(new Exception(), $"Unknown worktree state for {path}");
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

    static void FetchAllRemotes(string workingDirectory)
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
            if (process != null)
            {
                if (!process.WaitForExit(60000))
                {
                    GitWizardLog.Log($"git fetch --all timed out for {workingDirectory}", GitWizardLog.LogType.Warning);
                    process.Kill();
                    return;
                }

                if (process.ExitCode != 0)
                {
                    var error = process.StandardError.ReadToEnd();
                    if (!string.IsNullOrEmpty(error))
                    {
                        GitWizardLog.Log($"git fetch --all reported: {error}", GitWizardLog.LogType.Warning);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception fetching remotes for {workingDirectory}");
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
        var defaultTip = defaultBranch?.Tip;
        if (defaultTip == null)
            return;

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
                var isDefault = branch.FriendlyName == defaultBranch!.FriendlyName;

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
            // This matches what 'git status' does automatically
            // TODO: Remove this workaround once LibGit2Sharp exposes GIT_STATUS_OPT_UPDATE_INDEX
            // See: https://github.com/libgit2/libgit2/discussions/6852
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
    /// the caller is already defensive enough — no need to duplicate it here.</para>
    /// <para>This enumerates ALL files recursively including <c>.git/</c> objects, LFS storage,
    /// build output, etc. For repos with large git object stores the first full scan may
    /// take a long time. The existing <see cref="RefreshStatus"/> timeout covers this.</para>
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
        catch (UnauthorizedAccessException)
        {
            // Some directories may not be accessible (e.g., $Recycle.Bin, System Volume Information)
        }
        catch (IOException)
        {
            // Handle potential I/O errors on corrupted or networked filesystems
        }
        catch (Exception exception)
        {
            // Catch-all for unexpected exceptions (PathTooLongException, ArgumentException,
            // etc.) — log and return partial result rather than crashing the refresh.
            GitWizardLog.LogException(exception, $"Exception enumerating files for {path}");
        }

        return totalSize;
    }
}
