using LibGit2Sharp;

namespace GitWizard;

[Serializable]
public partial class GitWizardRepository
{
    /// <summary>
    /// RefreshError text set when a cached path exists but no longer resolves to a valid git
    /// repository. Shared with <see cref="GitWizardReport"/> so it can distinguish this "stale
    /// cache entry" case (safe to prune) from a merely missing directory (transient, kept).
    /// </summary>
    internal const string NotAGitRepositoryError = "Not a git repository";

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

    /// <summary>Commits on the current branch's upstream not reachable from the current branch - i.e. how far behind the remote this checkout is. 0 when there is no upstream. See <see cref="BranchInfo.BehindDefault"/> for the unrelated local-default comparison.</summary>
    public int BehindRemoteCount { get; private set; }

    /// <summary>Commits on the current branch not reachable from its upstream - i.e. unpushed commits on the checked-out branch. 0 when there is no upstream.</summary>
    public int AheadOfRemoteCount { get; private set; }

    /// <summary>When the last successful <c>fetchRemotes</c> refresh completed; null if this repository has never been fetched by GitWizard. Shown beside <see cref="BehindRemoteCount"/> so a stale comparison is visible.</summary>
    public DateTimeOffset? LastFetchTime { get; private set; }

    /// <summary>
    /// True when this checkout is safe to publish from: no pending changes, not behind its
    /// upstream remote, and currently on the default branch - the literal
    /// Asset-Store-submission check from the founding UniMerge incident (git-wizard#78 point 4).
    /// Purely derived from other already-serialized fields (not its own stored/private-set
    /// field), so it stays correct even against a report deserialized from an older cache rather
    /// than freshly refreshed in-process.
    /// </summary>
    public bool IsPublishReady =>
        !HasPendingChanges && BehindRemoteCount == 0 &&
        !string.IsNullOrEmpty(CurrentBranch) && CurrentBranch == DefaultBranch;

    GitWizardRepository() { }

    public GitWizardRepository(string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
    }

    public void Refresh(IUpdateHandler? updateHandler = null, bool fetchRemotes = false,
        bool deepRefresh = false, bool allBranches = false, bool computeLocalCommitCount = true)
    {
        if (string.IsNullOrEmpty(WorkingDirectory) || !Directory.Exists(WorkingDirectory))
        {
            GitWizardLog.Log($"Working directory {WorkingDirectory} is invalid.", GitWizardLog.LogType.Error);
            return;
        }

        if (fetchRemotes && FetchAllRemotes(WorkingDirectory))
            LastFetchTime = DateTimeOffset.Now;

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
                MarkRefreshFailed(NotAGitRepositoryError, updateHandler);
                return;
            }

            using var repository = new Repository(WorkingDirectory);
            IsRefreshing = true;
            RefreshSubmodules(updateHandler, repository, WorkingDirectory);
            CheckSubmoduleHealth(repository, WorkingDirectory);

            // Worktrees can't have worktrees, and trying to refresh them will cause an infinite loop
            if (!IsWorktree)
                RefreshWorktrees(updateHandler, repository);

            RefreshHeadInfo(repository);
            RefreshPendingChangesStatus(repository, deepRefresh);
            RefreshRemoteUrls(repository);
            if (computeLocalCommitCount)
                RefreshLocalOnlyCommits(repository);
            RefreshBranchDivergence(repository, allBranches);
            RefreshRemoteDivergence(repository);
            RefreshAuthorEmails(repository);
            RefreshRecentCommits(repository);
            RefreshSizeOnDisk(WorkingDirectory);

            IsRefreshing = false;
            NotifyRefreshCompleted(updateHandler);
        }
        catch (Exception exception)
        {
            RefreshError = exception.Message;
            GitWizardLog.LogException(exception, $"Exception thrown trying to refresh {WorkingDirectory}");
            IsRefreshing = false;
            NotifyRefreshCompleted(updateHandler);
        }
    }

    void RefreshHeadInfo(Repository repository)
    {
        CurrentBranch = repository.Head.FriendlyName;
        IsDetachedHead = repository.Head.Reference is not SymbolicReference;
        if (IsDetachedHead)
            FindMatchingBranch(repository);
        LastCommitDate = repository.Head.Tip?.Author.When;
        if (LastCommitDate.HasValue)
            DaysSinceLastCommit = (int)(DateTimeOffset.Now - LastCommitDate.Value).TotalDays;
    }

    void RefreshPendingChangesStatus(Repository repository, bool deepRefresh)
    {
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
    }

    void RefreshRemoteUrls(Repository repository)
    {
        RemoteUrls.Clear();
        foreach (var remote in repository.Network.Remotes)
        {
            if (!string.IsNullOrEmpty(remote.Url))
                RemoteUrls.Add(remote.Url);
        }
    }

    /// <summary>
    /// Count commits reachable from any LOCAL branch but not from any
    /// remote-tracking branch - genuinely unpushed commits, counted once
    /// across all local branches. The old per-branch approach counted an
    /// untracked branch's ENTIRE history as unpushed, so already-pushed
    /// mainline commits were multiply-counted and the total could exceed
    /// the repo's commit count. With no remotes, nothing is excluded and
    /// every local commit counts (a brand-new repo is all-local).
    /// </summary>
    /// <param name="repository">The repository to scan.</param>
    void RefreshLocalOnlyCommits(Repository repository)
    {
        LocalOnlyCommits = false;
        LocalCommitCount = 0;

        try
        {
            var localTips = repository.Branches
                .Where(branch => !branch.IsRemote)
                .Select(branch => branch.Tip)
                .Where(tip => tip != null)
                .ToList();

            if (localTips.Count > 0)
            {
                var filter = new CommitFilter { IncludeReachableFrom = localTips };

                var remoteTips = repository.Branches
                    .Where(branch => branch.IsRemote)
                    .Select(branch => branch.Tip)
                    .Where(tip => tip != null)
                    .ToList();
                if (remoteTips.Count > 0)
                    filter.ExcludeReachableFrom = remoteTips;

                LocalCommitCount = repository.Commits.QueryBy(filter).Count();
                LocalOnlyCommits = LocalCommitCount > 0;
            }
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception counting local-only commits for {WorkingDirectory}");
        }
    }

    void RefreshBranchDivergence(Repository repository, bool allBranches)
    {
        try
        {
            CollectBranches(repository, allBranches);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception collecting branches for {WorkingDirectory}");
        }
    }

    // Cache author emails for "My Repositories" filter
    void RefreshAuthorEmails(Repository repository)
    {
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
    }

    // Collect recent commits for projdash/LLM consumption
    void RefreshRecentCommits(Repository repository)
    {
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
    }

    void RefreshSizeOnDisk(string workingDirectory)
    {
        try
        {
            SizeOnDisk = ComputeDirectorySize(workingDirectory);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception computing size for {WorkingDirectory}");
        }
    }

    void NotifyRefreshCompleted(IUpdateHandler? updateHandler)
    {
        try
        {
            updateHandler?.OnRepositoryRefreshCompleted(this);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception thrown by Refresh OnRepositoryRefreshCompleted callback.");
        }
    }

}
