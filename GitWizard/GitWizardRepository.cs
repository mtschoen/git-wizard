using LibGit2Sharp;

namespace GitWizard;

[Serializable]
public partial class GitWizardRepository
{
    public string? WorkingDirectory { get; private set; }
    public string? CurrentBranch { get; private set; }
    public bool IsDetachedHead { get; private set; }
    public bool HasPendingChanges { get; private set; }
    public int NumberOfPendingChanges { get; private set; }
    public bool IsWorktree { get; private set; }
    public SortedDictionary<string, GitWizardRepository?>? Submodules { get; private set; }
    public SortedDictionary<string, GitWizardRepository?>? Worktrees { get; private set; }

    public bool HasSubmoduleIssues { get; private set; }
    public SortedDictionary<string, SubmoduleHealthInfo> SubmoduleHealth { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

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
            // Probe with IsValid before constructing Repository: a cached path whose .git is gone makes
            // the Repository ctor throw RepositoryNotFoundException on every refresh (first-chance
            // exception spam in the log). IsValid returns false for that case without throwing, so we
            // skip it cleanly. IsValid CAN still throw for other libgit2 errors (e.g. git's "not owned
            // by current user" ownership protection) -- those fall through to the catch below and
            // surface as a normal refresh error, exactly as before. Hence the check lives inside the
            // try. (Pruning stale cache entries is tracked separately.)
            if (!Repository.IsValid(WorkingDirectory))
            {
                GitWizardLog.Log($"Working directory {WorkingDirectory} is not a git repository; skipping refresh.", GitWizardLog.LogType.Warning);
                MarkRefreshFailed("Not a git repository", updateHandler);
                return;
            }

            var repository = new Repository(WorkingDirectory);
            IsRefreshing = true;
            RefreshSubmodules(updateHandler, repository, WorkingDirectory);
            CheckSubmoduleHealth(repository, WorkingDirectory);

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

}
