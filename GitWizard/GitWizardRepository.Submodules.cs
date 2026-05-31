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

        if (indexCommit != null && !indexCommit.Equals(workDirCommit))
        {
            info.Status = SubmoduleHealthStatus.WrongRef;
            info.Issues.Add(
                $"'{path}' is checked out at {Shorten(info.ActualCommitSha)} but the superproject expects {Shorten(indexCommit.Sha)}");
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
