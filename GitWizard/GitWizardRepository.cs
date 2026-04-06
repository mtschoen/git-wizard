using LibGit2Sharp;
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

    GitWizardRepository() { }

    public GitWizardRepository(string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
    }

    public void Refresh(IUpdateHandler? updateHandler = null, bool fetchRemotes = false,
        bool deepRefresh = false)
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
}