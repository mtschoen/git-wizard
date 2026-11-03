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

    const int AuthorEmailCommitLimit = 200;
    const int RecentCommitLimit = 10;

    public string? WorkingDirectory { get; private set; }
    public string? CurrentBranch { get; private set; }
    public bool IsDetachedHead { get; private set; }
    public bool HasPendingChanges { get; internal set; }
    public int NumberOfPendingChanges { get; private set; }
    public bool IsWorktree { get; internal set; }
    public SortedDictionary<string, GitWizardRepository?>? Submodules { get; private set; }
    public SortedDictionary<string, GitWizardRepository?>? Worktrees { get; private set; }

    public bool HasSubmoduleIssues { get; private set; }
    public SortedDictionary<string, SubmoduleHealthInfo> SubmoduleHealth { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsRefreshing { get; private set; }
    public bool LocalOnlyCommits { get; internal set; }
    public int LocalCommitCount { get; private set; }
    public List<string> RemoteUrls { get; private set; } = [];
    public DateTimeOffset? LastCommitDate { get; internal set; }
    public double RefreshTimeSeconds { get; set; }
    public double StatusTimeSeconds { get; set; }
    public bool UseCliStatus { get; set; }

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

    /// <summary>Number of stashes. Always 0 on a linked worktree, whose stashes belong to the main checkout's row.</summary>
    public int StashCount { get; set; }

    /// <summary>Stashes whose every change is already on the base branch (content tier), so safe to drop.</summary>
    public int MergedStashCount { get; set; }

    /// <summary>
    /// Local branches with commits on no remote-tracking ref that are not merged into the base.
    /// 0 when the refresh skipped local-commit counting.
    /// </summary>
    public int LocalOnlyBranchCount { get; set; }

    /// <summary>Tip author date of the oldest branch counted in <see cref="LocalOnlyBranchCount"/>; null when there is none.</summary>
    public DateTimeOffset? OldestLocalOnlyBranchDate { get; set; }

    /// <summary>
    /// Tips of local branches that have commits on no remote-tracking ref, collected by
    /// <see cref="RefreshLocalOnlyCommits"/> for the branch phase. Null when that phase was
    /// skipped. A field, so it is never serialized.
    /// </summary>
    HashSet<string>? _localOnlyTipShas;

    /// <summary>Commits on the current branch's upstream not reachable from the current branch - i.e. how far behind the remote this checkout is. 0 when there is no upstream. See <see cref="BranchInfo.BehindDefault"/> for the unrelated local-default comparison.</summary>
    public int BehindRemoteCount { get; internal set; }

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
        {
            return false;
        }

        if (Submodules == null)
        {
            return true;
        }

        foreach (var sub in Submodules.Values)
        {
            if (sub?.IsSubmodulePublishReady() == false)
            {
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
    /// call sites into an undisposed-object analyzer finding for no behavioral gain. A dedicated
    /// lock gives the same per-object mutual exclusion without that liability.
    /// </summary>
    readonly Lock _refreshGate = new();
    RepositoryRefreshOutcome _refreshOutcome;

    internal GitWizardRepository() { }

    public GitWizardRepository(string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
    }

    public void Refresh(IUpdateHandler? updateHandler = null, bool fetchRemotes = false,
        bool deepRefresh = false, bool allBranches = false, bool computeLocalCommitCount = true,
        CancellationToken cancellationToken = default) =>
        _ = RefreshWithOutcome(updateHandler, fetchRemotes, deepRefresh, allBranches, computeLocalCommitCount,
            new GitWizardRepositoryRefreshContext(null, cancellationToken));

    internal RepositoryRefreshOutcome RefreshWithOutcome(IUpdateHandler? updateHandler = null,
        bool fetchRemotes = false, bool deepRefresh = false, bool allBranches = false,
        bool computeLocalCommitCount = true, GitWizardRepositoryRefreshContext sizeContext = default)
    {
        // Serialize overlapping Refresh calls on this object: RefreshSubmodules and
        // RefreshWorktrees recurse into Refresh on DIFFERENT GitWizardRepository objects (their
        // own submodule/worktree instances, each with their own _refreshGate), so taking this
        // lock here cannot self-deadlock.
        using var diagnosticScope = UnresolvedUpstreamDiagnosticScope.Open();
        lock (_refreshGate)
        {
            RefreshLocked(updateHandler, fetchRemotes, deepRefresh, allBranches, computeLocalCommitCount, sizeContext);
            return _refreshOutcome;
        }
    }

    internal const string OwnershipBlockedError = "ownership-blocked: needs safe.directory";

    void RefreshLocked(IUpdateHandler? updateHandler, bool fetchRemotes, bool deepRefresh,
        bool allBranches, bool computeLocalCommitCount, GitWizardRepositoryRefreshContext sizeContext)
    {
        var cancellationToken = sizeContext.CancellationToken;
        _refreshOutcome = RepositoryRefreshOutcome.Success;
        if (string.IsNullOrEmpty(WorkingDirectory) || !Directory.Exists(WorkingDirectory))
        {
            _refreshOutcome = RepositoryRefreshOutcome.TransientFailure;
            // Expected for short-lived repositories (CI/test scratch repos under temp, deleted
            // between discovery and refresh): not an error, and Live drops the entry quietly.
            GitWizardLog.Log($"Working directory {WorkingDirectory} is invalid.", GitWizardLog.LogType.Debug);
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (fetchRemotes && FetchAllRemotes(WorkingDirectory))
            {
                LastFetchTime = DateTimeOffset.Now;
            }

            GitWizardLog.Log($"Refreshing {WorkingDirectory}");

            // Probe with IsValid before constructing Repository: a cached path whose .git is gone makes
            // the Repository ctor throw RepositoryNotFoundException on every refresh (first-chance
            // exception spam in the log). IsValid returns false for that case without throwing, so we
            // skip it cleanly. Other libgit2 errors reach the catch below for ownership and unsupported-
            // format classification, so the check lives inside the try.
            if (!Repository.IsValid(WorkingDirectory))
            {
                _refreshOutcome = RepositoryRefreshOutcome.NonRepository;
                GitWizardLog.Log($"Working directory {WorkingDirectory} is not a git repository; skipping refresh.", GitWizardLog.LogType.Warning);
                MarkRefreshFailed(NotAGitRepositoryError, updateHandler);
                return;
            }

            using var repository = new Repository(WorkingDirectory);
            IsRefreshing = true;
            RefreshError = null;
            RunRefreshPhases(updateHandler, repository, deepRefresh, allBranches, computeLocalCommitCount, sizeContext);
            IsRefreshing = false;
            NotifyRefreshCompleted(updateHandler);
        }
        catch (OperationCanceledException)
        {
            _refreshOutcome = RepositoryRefreshOutcome.Cancelled;
            GitWizardLog.Log($"Refresh timed out for {WorkingDirectory}", GitWizardLog.LogType.Warning);
            MarkRefreshFailed("Timed out", updateHandler);
        }
        catch (Exception exception)
        {
            if (IsOwnershipBlocked(exception))
            {
                _refreshOutcome = RepositoryRefreshOutcome.OwnershipBlocked;
                GitWizardLog.Log($"{OwnershipBlockedError}: {WorkingDirectory}", GitWizardLog.LogType.Warning);
                MarkRefreshFailed(OwnershipBlockedError, updateHandler);
                return;
            }

            if (TryFormatUnsupportedFormatError(exception, out var unsupportedFormatError))
            {
                _refreshOutcome = RepositoryRefreshOutcome.TransientFailure;
                GitWizardLog.Log($"{unsupportedFormatError}: {WorkingDirectory}", GitWizardLog.LogType.Warning);
                MarkRefreshFailed(unsupportedFormatError, updateHandler);
                return;
            }

            _refreshOutcome = RepositoryRefreshOutcome.TransientFailure;
            RefreshError = exception.Message;
            GitWizardLog.LogException(exception, $"Exception thrown trying to refresh {WorkingDirectory}");
            IsRefreshing = false;
            NotifyRefreshCompleted(updateHandler);
        }
    }

    internal static bool IsOwnershipBlocked(Exception exception) =>
        exception is LibGit2SharpException &&
        exception.Message.StartsWith("repository path '", StringComparison.Ordinal) &&
        exception.Message.EndsWith("' is not owned by current user", StringComparison.Ordinal);

    static bool TryFormatUnsupportedFormatError(Exception exception, out string refreshError)
    {
        if (exception is LibGit2SharpException)
        {
            if (exception.Message.Contains("extensions.refstorage", StringComparison.OrdinalIgnoreCase))
            {
                refreshError = "Unsupported repository format: reftable (extensions.refstorage)";
                return true;
            }

            if (exception.Message.Contains("unknown object format", StringComparison.OrdinalIgnoreCase))
            {
                refreshError = $"Unsupported repository format: {exception.Message}";
                return true;
            }
        }

        refreshError = string.Empty;
        return false;
    }

    /// <summary>
    /// Runs the refresh phases against an already-opened repository handle, checking the refresh context's
    /// cancellation token and firing <see cref="PhaseObserverForTesting"/>
    /// before each one with its zero-based index in execution order. Split out of <see
    /// cref="RefreshLocked"/> purely to keep that method short.
    /// </summary>
    void RunRefreshPhases(IUpdateHandler? updateHandler, Repository repository,
        bool deepRefresh, bool allBranches, bool computeLocalCommitCount, GitWizardRepositoryRefreshContext sizeContext)
    {
        var cancellationToken = sizeContext.CancellationToken;
        var workingDirectory = WorkingDirectory ?? string.Empty;
        var phaseIndex = 0;
        HashSet<string> refreshedSubmoduleKeys = [];
        Step(() => refreshedSubmoduleKeys = RefreshSubmodules(updateHandler, repository, workingDirectory, cancellationToken));
        Step(() => CheckSubmoduleHealth(repository, workingDirectory));
        if (!IsWorktree)
        {
            Step(() => RefreshWorktrees(updateHandler, repository, cancellationToken));
        }

        Step(() => RefreshHeadInfo(repository));
        Step(() =>
        {
            RefreshPendingChangesStatus(repository, deepRefresh);
            AddModifiedSubmodulesToPendingChanges(refreshedSubmoduleKeys);
        });
        Step(() => RefreshRemoteUrls(repository));
        _localOnlyTipShas = null;
        if (computeLocalCommitCount)
        {
            Step(() => RefreshLocalOnlyCommits(repository));
        }

        Step(() => RefreshBranchDivergence(repository, allBranches));
        Step(() => RefreshRemoteDivergence(repository));
        Step(() => RefreshAuthorEmailsAndRecentCommits(repository));
        PhaseObserverForTesting?.Invoke(phaseIndex);
        cancellationToken.ThrowIfCancellationRequested();
        RefreshSizeOnDisk(workingDirectory, sizeContext.SizeTally);
        return;

        void Step(Action action)
        {
            PhaseObserverForTesting?.Invoke(phaseIndex++);
            cancellationToken.ThrowIfCancellationRequested();
            action();
        }
    }

    void RefreshHeadInfo(Repository repository)
    {
        CurrentBranch = repository.Head.FriendlyName;
        IsDetachedHead = repository.Head.Reference is not SymbolicReference;
        if (IsDetachedHead)
        {
            FindMatchingBranch(repository);
        }
        else
        {
            MatchingBranchName = null;
        }

        LastCommitDate = repository.Head.Tip?.Author.When;
        if (LastCommitDate.HasValue)
        {
            DaysSinceLastCommit = (int)(DateTimeOffset.Now - LastCommitDate.Value).TotalDays;
        }
    }

    void RefreshRemoteUrls(Repository repository)
    {
        RemoteUrls.Clear();
        RemoteUrls.AddRange(repository.Network.Remotes.Select(r => r.Url).Where(url => !string.IsNullOrEmpty(url)));
    }

    // One revwalk feeds both the "My Repositories" author-email cache and the recent-commit
    // summary for projdash/LLM consumption. CommitSortStrategies.None skips libgit2's eager
    // time-sort preparation of the whole history; parents are still inserted by date.
    internal void RefreshAuthorEmailsAndRecentCommits(Repository repository)
    {
        var authorEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recentCommits = new List<GitWizardCommitInfo>(RecentCommitLimit);
        try
        {
            var filter = new CommitFilter { SortBy = CommitSortStrategies.None };
            foreach (var commit in repository.Commits.QueryBy(filter).Take(AuthorEmailCommitLimit))
            {
                authorEmails.Add(commit.Author.Email);
                if (recentCommits.Count < RecentCommitLimit)
                {
                    recentCommits.Add(new GitWizardCommitInfo
                    {
                        Hash = commit.Sha[..7],
                        Message = commit.MessageShort,
                        Date = commit.Author.When,
                        AuthorEmail = commit.Author.Email
                    });
                }
            }

            AuthorEmails = authorEmails;
        }
        catch (Exception exception)
        {
            LogRepositoryException(exception,
                $"Exception collecting author emails and recent commits for {WorkingDirectory}");
        }

        // The recent commits keep whatever the walk produced before a failure, so a fault
        // late in the 200-commit walk still reports the ten newest commits.
        RecentCommits = recentCommits;
    }

    internal void NotifyRefreshCompleted(IUpdateHandler? updateHandler)
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
