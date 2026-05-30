using System.Runtime.InteropServices;
using LibGit2Sharp;

namespace GitWizard;

public enum SubmoduleHealthStatus
{
    Healthy,
    Uninitialized,
    WrongRef,
    MissingFromIndex,
    MissingFromGitmodules,
}

public class SubmoduleHealthInfo
{
    // Serialized to report.json as part of SubmoduleHealth; the getter is exercised by
    // System.Text.Json, which ReSharper's usage analysis doesn't account for.
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public string Path { get; set; } = string.Empty;
    public string? ExpectedCommitSha { get; set; }
    public string? ActualCommitSha { get; set; }
    public SubmoduleHealthStatus Status { get; set; }
    public List<string> Issues { get; set; } = new();
}

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
                                GitWizardLog.LogException(new InvalidOperationException("Submodule in unknown state"), $"Unknown submodule state for {path}");
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

    /// <summary>
    /// Reconcile the submodules declared in <c>.gitmodules</c> against the gitlinks
    /// recorded in the index and the state of each checked-out submodule, recording any
    /// mismatches in <see cref="SubmoduleHealth"/>. Healthy submodules are not recorded.
    /// </summary>
    void CheckSubmoduleHealth(Repository repository, string workingDirectory)
    {
        HasSubmoduleIssues = false;
        SubmoduleHealth = new SortedDictionary<string, SubmoduleHealthInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Submodule name -> path, as declared in .gitmodules.
            var declared = ParseGitmodules(workingDirectory);

            // Paths the index records as gitlinks (committed submodule pointers).
            var indexedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in repository.Index)
            {
                if (entry.Mode == Mode.GitLink)
                    indexedPaths.Add(entry.Path);
            }

            var allPaths = new HashSet<string>(declared.Values, StringComparer.OrdinalIgnoreCase);
            allPaths.UnionWith(indexedPaths);

            foreach (var path in allPaths)
            {
                var name = declared.FirstOrDefault(
                    entry => string.Equals(entry.Value, path, StringComparison.OrdinalIgnoreCase)).Key;
                var isDeclared = !string.IsNullOrEmpty(name);
                var isIndexed = indexedPaths.Contains(path);
                var info = new SubmoduleHealthInfo { Path = path };

                if (isDeclared && !isIndexed)
                {
                    info.Status = SubmoduleHealthStatus.MissingFromIndex;
                    info.Issues.Add($"'{path}' is declared in .gitmodules but missing from the index");
                }
                else if (!isDeclared && isIndexed)
                {
                    info.Status = SubmoduleHealthStatus.MissingFromGitmodules;
                    info.Issues.Add($"'{path}' is recorded in the index but missing from .gitmodules");
                }
                else
                {
                    EvaluateCheckedOutSubmodule(repository, name!, path, info);
                }

                if (info.Status != SubmoduleHealthStatus.Healthy)
                    SubmoduleHealth[path] = info;
            }

            HasSubmoduleIssues = SubmoduleHealth.Count > 0;
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception checking submodule health for {WorkingDirectory}");
        }
    }

    static void EvaluateCheckedOutSubmodule(Repository repository, string name, string path, SubmoduleHealthInfo info)
    {
        var submodule = repository.Submodules[name];
        var indexCommit = submodule?.IndexCommitId;
        var workDirCommit = submodule?.WorkDirCommitId;

        // No checked-out commit means the submodule was never initialized
        // (e.g. cloned without --recursive).
        if (workDirCommit == null)
        {
            info.Status = SubmoduleHealthStatus.Uninitialized;
            info.Issues.Add($"'{path}' is not initialized (run 'git submodule update --init')");
            return;
        }

        info.ExpectedCommitSha = indexCommit?.Sha;
        info.ActualCommitSha = workDirCommit.Sha;

        if (indexCommit != null && !indexCommit.Equals(workDirCommit))
        {
            info.Status = SubmoduleHealthStatus.WrongRef;
            info.Issues.Add(
                $"'{path}' is checked out at {Shorten(info.ActualCommitSha)} but the superproject expects {Shorten(info.ExpectedCommitSha!)}");
        }
    }

    /// <summary>
    /// Parse <c>.gitmodules</c> into a map of submodule name to declared path. Returns an
    /// empty map when the file is absent.
    /// </summary>
    static Dictionary<string, string> ParseGitmodules(string workingDirectory)
    {
        var result = new Dictionary<string, string>();
        var gitmodulesPath = Path.Combine(workingDirectory, ".gitmodules");
        if (!File.Exists(gitmodulesPath))
            return result;

        string? currentName = null;
        foreach (var rawLine in File.ReadLines(gitmodulesPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var section = line[1..^1].Trim();
                currentName = section.StartsWith("submodule ", StringComparison.OrdinalIgnoreCase)
                    ? section["submodule ".Length..].Trim().Trim('"')
                    : null;
            }
            else if (currentName != null && line.StartsWith("path", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2)
                    result[currentName] = parts[1].Trim();
            }
        }

        return result;
    }

    static string Shorten(string sha) => sha.Length <= 7 ? sha : sha[..7];

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
