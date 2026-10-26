using GitWizard;

namespace GitWizardUI.ViewModels;

static class RepositoryGroupKeys
{
    internal static Dictionary<string, List<RepositoryNodeViewModel>> GroupRepositories(
        List<RepositoryNodeViewModel> repositories, GroupMode mode)
    {
        var groups = new Dictionary<string, List<RepositoryNodeViewModel>>();
        foreach (var repo in repositories)
        {
            var keys = GetGroupKeys(repo, mode);
            foreach (var key in keys)
            {
                if (!groups.TryGetValue(key, out var list))
                {
                    list = [];
                    groups[key] = list;
                }

                list.Add(repo);
            }
        }

        return groups;
    }

    internal static List<string> GetGroupKeys(RepositoryNodeViewModel repository, GroupMode mode)
    {
        switch (mode)
        {
            case GroupMode.Drive:
                var path = repository.WorkingDirectory;
                if (string.IsNullOrEmpty(path))
                {
                    return ["(unknown)"];
                }

                var root = Path.GetPathRoot(path);
                return [string.IsNullOrEmpty(root) ? "(unknown)" : NormalizeDriveGroupKey(root)];
            case GroupMode.RemoteUrl:
                var urls = repository.Repository.RemoteUrls;
                // Normalize remote URLs for grouping (strip .git suffix, normalize casing).
                return urls.Count == 0
                    ? ["(no remote)"]
                    : [.. urls.Select(NormalizeRemoteUrl).Distinct()];
            case GroupMode.None:
            default:
                return ["(unknown)"];
        }
    }

    // Discovered repository paths are cached lowercase (~/.GitWizard/repositories.txt) while
    // Live-created ones arrive with OS-native drive-letter case (issue #240's evidence), so the
    // drive-root group key must be normalized to one case - otherwise "c:\" and "C:\" become
    // separate groups for what is the same drive. Path.GetPathRoot returns "/" unchanged on
    // Unix, so this is a no-op there.
    static string NormalizeDriveGroupKey(string root) => root.ToUpperInvariant();

    internal static string NormalizeRemoteUrl(string url)
    {
        url = url.Trim();
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^4];
        }

        // Normalize SSH URLs (git@github.com:user/repo) to match HTTPS style for grouping
        if (!url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = url[8..];
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                url = url[7..];
            }

            return url.ToLowerInvariant();
        }

        var colonIndex = url.IndexOf(':');
        if (colonIndex <= 0)
        {
            return url.ToLowerInvariant();
        }

        var host = url[4..colonIndex];
        var path = url[(colonIndex + 1)..];
        return $"{host}/{path}".ToLowerInvariant();
    }

    internal static string? FindRenamedRepo(GitWizardRepository erroredRepository, string oldPath,
        IReadOnlyDictionary<string, GitWizardRepository> healthyRepositories)
    {
        if (erroredRepository.RemoteUrls.Count == 0)
        {
            return null;
        }

        foreach (var remoteUrl in erroredRepository.RemoteUrls)
        {
            var normalizedRemote = NormalizeRemoteUrl(remoteUrl);
            foreach (var (path, healthyRepo) in healthyRepositories)
            {
                if (path == oldPath)
                {
                    continue;
                }

                foreach (var healthyRemote in healthyRepo.RemoteUrls)
                {
                    if (NormalizeRemoteUrl(healthyRemote) == normalizedRemote)
                    {
                        return path;
                    }
                }
            }
        }

        return null;
    }
}
