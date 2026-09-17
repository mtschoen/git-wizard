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

    /// <summary>Error message from the most recent failed refresh; cleared back to null once a
    /// later refresh begins succeeding, so this never reports a stale failure past a successful
    /// refresh.</summary>
    public string? RefreshError { get; set; }
    public HashSet<string>? AuthorEmails { get; private set; }
    public List<GitWizardCommitInfo>? RecentCommits { get; private set; }
    public int? DaysSinceLastCommit { get; private set; }
    public string? MatchingBranchName { get; private set; }
    public string? DefaultBranch { get; private set; }
    public List<BranchInfo>? Branches { get; set; }

    /// <summary>Commits on the current branch's upstream not reachable from the current branch - i.e. how far behind the remote this checkout is. 0 when there is no upstream. See <see cref="BranchInfo.BehindDefault"/> for the unrelated local-default comparison.</summary>
    public int BehindRemoteCount { get; private set; }

    /// <summary>Commits on the current branch not reachable from its upstream - i.e. unpushed commits on the checked-out branch. 0 when there is no upstream.</summary>
    public int AheadOfRemoteCount { get; private set; }

    /// <summary>When the last successful <c>fetchRemotes</c> refresh completed; null if this repository has never been fetched by GitWizard. Shown beside <see cref="BehindRemoteCount"/> so a stale comparison is visible.</summary>
    public DateTimeOffset? LastFetchTime { get; private set; }

    /// <summary>
    /// True when this checkout is safe to publish from: no pending changes, not behind its
    /// upstream remote, currently on the default branch, and all initialized submodules are
    /// healthy and up-to-date - the literal Asset-Store-submission check from the founding
    /// UniMerge incident (git-wizard#78 point 4). A submodule with any health issue
    /// (Uninitialized, WrongRef, StaleUpstream) or pending changes makes this false;
    /// submodules of submodules are recursed.
    /// Purely derived from other already-serialized fields (not its own stored/private-set
    /// field), so it stays correct even against a report deserialized from an older cache rather
    /// than freshly refreshed in-process.
    /// </summary>
    public bool IsPublishReady =>
        !HasPendingChanges && BehindRemoteCount == 0 &&
        !string.IsNullOrEmpty(CurrentBranch) && CurrentBranch == DefaultBranch &&
        IsSubmodulePublishReady();

    bool IsSubmodulePublishReady()
    {
        if (SubmoduleHealth.Values.Any(health => health.Status != SubmoduleHealthStatus.Healthy))
            return false;

        if (Submodules != null)
        {
            foreach (var sub in Submodules.Values)
            {
                if (sub is not null && !sub.IsSubmodulePublishReady())
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Test-only seam invoked at each phase boundary inside <see cref="Refresh"/> with the
    /// zero-based index of the phase about to run, in execution order. Later phases of
    /// docs/refresh-concurrency-review.md (cancellation between phases, interleaving assertions)
    /// reuse this hook, so it stays minimal: one integer, no payload.
    /// </summary>
    internal Action<int>? PhaseObserverForTesting { get; set; }

    /// <summary>
    /// Serializes overlapping <see cref="Refresh"/> calls on THIS repository object so no two
    /// threads write its fields at once (docs/refresh-concurrency-review.md hazard 3 - e.g. a
    /// Live-watch refresh racing a batch refresh on the same object). A field rather than a
    /// property, so <c>System.Text.Json</c> never sees it as part of the serialization surface.
    /// Deliberately a plain lock object rather than the <c>SemaphoreSlim</c> sketched in the
    /// review doc: <c>SemaphoreSlim</c> is <see cref="IDisposable"/>, and <see
    /// cref="GitWizardRepository"/> is constructed at every discovery and refresh call site
    /// without ever being disposed, so making the type disposable would turn every one of those
    /// call sites into an undisposed-object analyzer finding for no behavioral gain. A monitor
    /// lock gives the same per-object mutual exclusion without that liability.
    /// </summary>
    readonly object _refreshGate = new();

    GitWizardRepository() { }

    public GitWizardRepository(string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
    }

    public void Refresh(IUpdateHandler? updateHandler = null, bool fetchRemotes = false,
        bool deepRefresh = false, bool allBranches = false, bool computeLocalCommitCount = true,
        CancellationToken cancellationToken = default)
    {
        // Serialize overlapping Refresh calls on this object: RefreshSubmodules and
        // RefreshWorktrees recurse into Refresh on DIFFERENT GitWizardRepository objects (their
        // own submodule/worktree instances, each with their own _refreshGate), so taking this
        // lock here cannot self-deadlock.
        using var diagnosticScope = UnresolvedUpstreamDiagnosticScope.Open();
        lock (_refreshGate)
        {
            RefreshLocked(updateHandler, fetchRemotes, deepRefresh, allBranches, computeLocalCommitCount, cancellationToken);
        }
    }

    void RefreshLocked(IUpdateHandler? updateHandler, bool fetchRemotes, bool deepRefresh,
        bool allBranches, bool computeLocalCommitCount, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(WorkingDirectory) || !Directory.Exists(WorkingDirectory))
        {
            // Expected for short-lived repositories (CI/test scratch repos under temp, deleted
            // between discovery and refresh): not an error, and Live drops the entry quietly.
            GitWizardLog.Log($"Working directory {WorkingDirectory} is invalid.", GitWizardLog.LogType.Debug);
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (fetchRemotes && FetchAllRemotes(WorkingDirectory))
                LastFetchTime = DateTimeOffset.Now;

            GitWizardLog.Log($"Refreshing {WorkingDirectory}");

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
            RefreshError = null;
            RunRefreshPhases(updateHandler, repository, deepRefresh, allBranches, computeLocalCommitCount, cancellationToken);
            IsRefreshing = false;
            NotifyRefreshCompleted(updateHandler);
        }
        catch (OperationCanceledException)
        {
            GitWizardLog.Log($"Refresh timed out for {WorkingDirectory}", GitWizardLog.LogType.Warning);
            MarkRefreshFailed("Timed out", updateHandler);
        }
        catch (Exception exception)
        {
            RefreshError = exception.Message;
            GitWizardLog.LogException(exception, $"Exception thrown trying to refresh {WorkingDirectory}");
            IsRefreshing = false;
            NotifyRefreshCompleted(updateHandler);
        }
    }

    /// <summary>
    /// Runs the refresh phases against an already-opened repository handle, checking
    /// <paramref name="cancellationToken"/> and firing <see cref="PhaseObserverForTesting"/>
    /// before each one with its zero-based index in execution order. Split out of <see
    /// cref="RefreshLocked"/> purely to keep that method short.
    /// </summary>
    void RunRefreshPhases(IUpdateHandler? updateHandler, Repository repository,
        bool deepRefresh, bool allBranches, bool computeLocalCommitCount, CancellationToken cancellationToken)
    {
        var workingDirectory = WorkingDirectory ?? string.Empty;
        var phaseIndex = 0;
        void Step(Action action)
        {
            PhaseObserverForTesting?.Invoke(phaseIndex++);
            cancellationToken.ThrowIfCancellationRequested();
            action();
        }

        Step(() => RefreshSubmodules(updateHandler, repository, workingDirectory, cancellationToken));
        Step(() => CheckSubmoduleHealth(repository, workingDirectory));
        if (!IsWorktree)
            Step(() => RefreshWorktrees(updateHandler, repository, cancellationToken));
        Step(() => RefreshHeadInfo(repository));
        Step(() => RefreshPendingChangesStatus(repository, deepRefresh));
        Step(() => RefreshRemoteUrls(repository));
        if (computeLocalCommitCount)
            Step(() => RefreshLocalOnlyCommits(repository));
        Step(() => RefreshBranchDivergence(repository, allBranches));
        Step(() => RefreshRemoteDivergence(repository));
        Step(() => RefreshAuthorEmails(repository));
        Step(() => RefreshRecentCommits(repository));
        PhaseObserverForTesting?.Invoke(phaseIndex);
        cancellationToken.ThrowIfCancellationRequested();
        RefreshSizeOnDisk(workingDirectory);
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
                NumberOfPendingChanges = status.Modified.Count() + status.Missing.Count() +
                    status.Staged.Count() + status.Removed.Count() + status.Added.Count() +
                    status.Untracked.Count() + status.RenamedInIndex.Count() + status.RenamedInWorkDir.Count();
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
        RemoteUrls.AddRange(repository.Network.Remotes.Select(r => r.Url).Where(url => !string.IsNullOrEmpty(url)));
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
            AuthorEmails = repository.Commits.Take(200)
                .Select(commit => commit.Author.Email)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
