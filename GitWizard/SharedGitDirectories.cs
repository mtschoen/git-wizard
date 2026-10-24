using LibGit2Sharp;

namespace GitWizard;

/// <summary>
/// Linked worktrees share branches and stashes with their main checkout, so whole-repository
/// scans (branch inventory, prune) should visit each shared git directory once.
/// </summary>
static class SharedGitDirectories
{
    /// <summary>
    /// Keep one path per shared git directory, in ordinal order: the main checkout when it is
    /// listed, else the first linked worktree. Paths that are not repositories are dropped; a path
    /// that fails to open is kept so the caller's scan reports the error for it.
    /// </summary>
    internal static List<string> OnePathPerCommonDirectory(IEnumerable<string> repositoryPaths)
    {
        var chosen = new Dictionary<string, (string Path, bool IsMain)>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        foreach (var path in new SortedSet<string>(repositoryPaths, StringComparer.Ordinal))
        {
            try
            {
                if (!Repository.IsValid(path))
                {
                    continue;
                }

                using var repository = new Repository(path);
                var (commonDirectory, isMain) = CommonDirectory(repository);
                if (!chosen.TryGetValue(commonDirectory, out var existing) || (isMain && !existing.IsMain))
                {
                    chosen[commonDirectory] = (path, isMain);
                }
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, $"Exception resolving the git directory of {path}");
                unreadable.Add(path);
            }
        }

        return chosen.Values.Select(entry => entry.Path).Concat(unreadable).Order(StringComparer.Ordinal).ToList();
    }

    static (string Path, bool IsMain) CommonDirectory(Repository repository)
    {
        var gitDirectory = Path.GetFullPath(repository.Info.Path);
        var commonDirFile = Path.Combine(gitDirectory, "commondir");
        if (!File.Exists(commonDirFile))
        {
            return (gitDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), true);
        }

        var target = File.ReadAllText(commonDirFile).Trim();
        return (Path.GetFullPath(Path.Combine(gitDirectory, target))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), false);
    }
}
