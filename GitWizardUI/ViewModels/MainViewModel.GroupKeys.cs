using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    static Dictionary<string, List<RepositoryNodeViewModel>> GroupRepositories(
        List<RepositoryNodeViewModel> repos, GroupMode mode)
    {
        var groups = new Dictionary<string, List<RepositoryNodeViewModel>>();
        foreach (var repo in repos)
        {
            var keys = GetGroupKeys(repo, mode);
            foreach (var key in keys)
            {
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<RepositoryNodeViewModel>();
                    groups[key] = list;
                }

                list.Add(repo);
            }
        }

        return groups;
    }

    static List<string> GetGroupKeys(RepositoryNodeViewModel repo, GroupMode mode)
    {
        if (mode == GroupMode.Drive)
        {
            var path = repo.WorkingDirectory;
            if (string.IsNullOrEmpty(path))
                return new List<string> { "(unknown)" };

            var root = Path.GetPathRoot(path);
            return new List<string> { string.IsNullOrEmpty(root) ? "(unknown)" : NormalizeDriveGroupKey(root) };
        }

        if (mode == GroupMode.RemoteUrl)
        {
            var urls = repo.Repository.RemoteUrls;
            if (urls.Count == 0)
                return new List<string> { "(no remote)" };

            // Normalize remote URLs for grouping (strip .git suffix, normalize casing)
            return urls.Select(NormalizeRemoteUrl).Distinct().ToList();
        }

        return new List<string> { "(unknown)" };
    }

    // Discovered repository paths are cached lowercase (~/.GitWizard/repositories.txt) while
    // Live-created ones arrive with OS-native drive-letter case (issue #240's evidence), so the
    // drive-root group key must be normalized to one case - otherwise "c:\" and "C:\" become
    // separate groups for what is the same drive. Path.GetPathRoot returns "/" unchanged on
    // Unix, so this is a no-op there.
    static string NormalizeDriveGroupKey(string root) => root.ToUpperInvariant();

    static string NormalizeRemoteUrl(string url)
    {
        url = url.Trim();
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        // Normalize SSH URLs (git@github.com:user/repo) to match HTTPS style for grouping
        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colonIndex = url.IndexOf(':');
            if (colonIndex > 0)
            {
                var host = url[4..colonIndex];
                var path = url[(colonIndex + 1)..];
                url = $"{host}/{path}";
            }
        }
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = url[8..];
        }
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            url = url[7..];
        }

        return url.ToLowerInvariant();
    }

    static string? FindRenamedRepo(GitWizardRepository erroredRepo, string oldPath,
        IReadOnlyDictionary<string, GitWizardRepository> healthyRepos)
    {
        if (erroredRepo.RemoteUrls.Count == 0)
            return null;

        foreach (var remoteUrl in erroredRepo.RemoteUrls)
        {
            var normalizedRemote = NormalizeRemoteUrl(remoteUrl);
            foreach (var (path, healthyRepo) in healthyRepos)
            {
                if (path == oldPath)
                    continue;
                foreach (var healthyRemote in healthyRepo.RemoteUrls)
                {
                    if (NormalizeRemoteUrl(healthyRemote) == normalizedRemote)
                        return path;
                }
            }
        }

        return null;
    }

    // Merges one group key's whole batch of new children into its header in one shot, instead of
    // AddToGroups's per-node calls (round-2 pr-crew review of PR #241/#243: see
    // MergeBatchIntoDisplay for why a per-row insert is O(n) per call). Creates the header fresh
    // when none exists yet (the common case for PrePopulateFromReport, which always starts with
    // empty groups/pending state) or bulk-merges into an existing/pending one (the case
    // AddRepositoriesBatch can hit, merging against a header a prior prepopulation or drain
    // already created).
    void MergeGroupBatch(string key, List<RepositoryNodeViewModel> bucket, bool sort = true)
    {
        var minGroupSize = _activeGroupMode == GroupMode.RemoteUrl ? 2 : 1;

        RepositoryNodeViewModel? header = null;
        foreach (var candidate in Repositories)
        {
            if (candidate.IsGroupHeader && candidate.GroupKey == key)
            {
                header = candidate;
                break;
            }
        }

        var wasPending = false;
        if (header == null && _pendingGroups.TryGetValue(key, out var pendingHeader))
        {
            header = pendingHeader;
            wasPending = true;
        }

        if (header != null)
        {
            if (sort)
            {
                MergeBatchIntoDisplay(header.Children, bucket);
            }
            else
            {
                foreach (var node in bucket)
                    header.Children.Add(node);
            }

            header.UpdateDisplayText();
            RefreshExpandedFlatChildren(header);

            if (wasPending && header.Children.Count >= minGroupSize)
            {
                _pendingGroups.Remove(key);
                Repositories.Add(header);
            }

            return;
        }

        header = RepositoryNodeViewModel.CreateGroupHeader(key);
        var nodesToAdd = sort ? SortBatchForDisplay(bucket) : bucket;
        foreach (var node in nodesToAdd)
            header.Children.Add(node);
        header.UpdateDisplayText();

        if (header.Children.Count >= minGroupSize)
            Repositories.Add(header);
        else
            _pendingGroups[key] = header;
    }

    // Rebuilds an expanded header's flat display rows from its own (just-merged) Children,
    // instead of computing per-row insertion points for a whole batch: cheaper than repeated
    // positional inserts, and always exactly consistent with Children's order. A header expanded
    // while a batch merge runs is rare (the common case is a scan streaming into collapsed
    // headers), so a full rebuild of just this header's own flat block is an acceptable cost
    // here - unlike the per-row arrival path, this is not called once per row.
    void RefreshExpandedFlatChildren(RepositoryNodeViewModel header)
    {
        if (!header.IsExpanded)
            return;

        var headerIndex = Repositories.IndexOf(header);
        if (headerIndex < 0)
            return;

        var removeFrom = headerIndex + 1;
        while (removeFrom < Repositories.Count && !Repositories[removeFrom].IsGroupHeader)
            Repositories.RemoveAt(removeFrom);

        var insertAt = removeFrom;
        foreach (var child in header.Children)
            InsertPositional(Repositories, insertAt++, child);
    }
}
