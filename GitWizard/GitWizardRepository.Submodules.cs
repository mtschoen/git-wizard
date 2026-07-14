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
    StaleUpstream,
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

    /// <summary>How many commits the superproject-recorded gitlink SHA is behind the tip of the submodule's default branch. Non-zero when the gitlink pointer is stale relative to the submodule's upstream (git-wizard#80).</summary>
    public int BehindUpstreamCount { get; set; }

    /// <summary>The full SHA of the superproject-recorded gitlink commit (the expected commit for this submodule).</summary>
    public string? GitLinkSha { get; set; }
}

public partial class GitWizardRepository
{
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
                else if (!string.IsNullOrEmpty(name))
                {
                    EvaluateCheckedOutSubmodule(repository, name, path, info);
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
        info.GitLinkSha = indexCommit?.Sha;

        if (indexCommit != null && !indexCommit.Equals(workDirCommit))
        {
            info.Status = SubmoduleHealthStatus.WrongRef;
            info.Issues.Add(
                $"'{path}' is checked out at {Shorten(info.ActualCommitSha)} but the superproject expects {Shorten(indexCommit.Sha)}");
            return;
        }

        // The gitlink and workdir checkout agree - check if the recorded pointer is stale
        // relative to the submodule's upstream (git-wizard#80).
        // Guard: indexCommit should not be null here (workdir is initialized, gitlink is in the index).
        // If it is, the submodule is in an inconsistent state and we skip stale-upstream analysis.
        if (indexCommit != null)
            CheckStaleUpstream(repository, path, indexCommit, info);
    }

    /// <summary>
    /// Resolve the default branch from the submodule repository itself (not the parent) and
    /// compare its tip - falling back to the remote-tracking branch if the checkout is detached
    /// at the gitlink - against the gitlink commit, recording <see cref="SubmoduleHealthStatus.StaleUpstream"/>
    /// when the submodule's upstream has moved on without the superproject's pointer.
    /// </summary>
    static void CheckStaleUpstream(Repository repository, string path, ObjectId indexCommit, SubmoduleHealthInfo info)
    {
        try
        {
            var submodulePath = Path.Combine(repository.Info.WorkingDirectory, path);

            var submoduleRepo = Repository.IsValid(submodulePath)
                ? new Repository(submodulePath)
                : null;

            try
            {
                var defaultBranch = submoduleRepo != null ? ResolveDefaultBranch(submoduleRepo) : null;
                var tip = defaultBranch?.Tip;
                var gitLinkSha = indexCommit.Sha;

                // If the default branch tip is the same as the gitlink (detached at gitlink),
                // try the remote-tracking branch instead, since the remote may have advanced.
                if (tip != null && tip.Sha == gitLinkSha)
                {
                    var defaultBranchName = defaultBranch?.FriendlyName ?? "main";
                    // Prefer "origin", fall back to the first available remote (null if there is none).
                    var remoteName = submoduleRepo?.Network.Remotes.FirstOrDefault(r => r.Name == "origin")?.Name
                        ?? submoduleRepo?.Network.Remotes.FirstOrDefault()?.Name;
                    var remoteBranch = remoteName != null ? submoduleRepo?.Branches[$"{remoteName}/{defaultBranchName}"] : null;
                    tip = remoteBranch?.Tip;
                }

                if (tip != null && submoduleRepo != null)
                {
                    // Look up the gitlink commit in the submodule's own database.
                    var submoduleDb = submoduleRepo.ObjectDatabase;
                    var gitLinkInSubmodule = submoduleRepo.Lookup<Commit>(indexCommit.Sha);
                    if (gitLinkInSubmodule != null)
                    {
                        var divergence = submoduleDb.CalculateHistoryDivergence(gitLinkInSubmodule, tip);
                        info.BehindUpstreamCount = divergence.BehindBy ?? 0;
                        if (info.BehindUpstreamCount > 0)
                        {
                            info.Status = SubmoduleHealthStatus.StaleUpstream;
                            info.Issues.Add(
                                $"'{path}' is behind upstream by {info.BehindUpstreamCount} commit(s) - the superproject-recorded pointer ({Shorten(gitLinkSha)}) is stale relative to the submodule's default branch ({Shorten(tip.Sha)})");
                        }
                    }
                }
            }
            finally
            {
                submoduleRepo?.Dispose();
            }
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception computing behind-upstream divergence for submodule '{path}' in {repository.Info.WorkingDirectory}");
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
}
