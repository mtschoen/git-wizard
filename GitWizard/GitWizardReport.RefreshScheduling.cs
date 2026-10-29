namespace GitWizard;

public partial class GitWizardReport
{
    internal IReadOnlyList<string> OrderPathsForRefresh(IEnumerable<string> paths)
    {
        var previousReport = GetCachedReport();
        return [.. paths.OrderByDescending(path => GetPreviousRefreshSeconds(path, previousReport))];
    }

    double GetPreviousRefreshSeconds(string path, GitWizardReport? previousReport)
    {
        if (TryGetRefreshSeconds(Repositories, path, out var seconds))
        {
            return seconds;
        }

        if (previousReport != null && !ReferenceEquals(previousReport, this) &&
            TryGetRefreshSeconds(previousReport.Repositories, path, out seconds))
        {
            return seconds;
        }

        return 0;
    }

    static bool TryGetRefreshSeconds(SortedDictionary<string, GitWizardRepository> repositories,
        string path, out double seconds)
    {
        lock (repositories)
        {
            if (repositories.TryGetValue(path, out var repository) &&
                double.IsFinite(repository.RefreshTimeSeconds) && repository.RefreshTimeSeconds > 0)
            {
                seconds = repository.RefreshTimeSeconds;
                return true;
            }
        }

        seconds = 0;
        return false;
    }
}
