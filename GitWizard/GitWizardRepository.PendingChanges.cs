using LibGit2Sharp;

namespace GitWizard;

public partial class GitWizardRepository
{
    bool _statusFallbackLogged;

    internal void RefreshPendingChangesStatus(Repository repository, bool deepRefresh,
        int trackedFileThreshold = RepositoryPendingChangesStatus.TrackedFileThreshold,
        Func<Repository, string[], GitCliResult>? runGit = null)
    {
        try
        {
            if (repository.Info.IsBare)
            {
                // A bare repository has no index or working tree to report status on.
                return;
            }

            // Refresh the index to update stale stat data (slow on large repos / slow drives).
            // Only run during deep refresh to keep auto-refresh fast.
            if (deepRefresh)
            {
                RefreshIndex(repository);
            }

            if (repository.Index.Count >= trackedFileThreshold && TryRefreshCliStatus(repository, runGit))
            {
                return;
            }

            var status = repository.RetrieveStatus();
            HasPendingChanges = status.IsDirty;
            // Type changes and conflicts are dirty but have no counted group.
            NumberOfPendingChanges = status.Modified.Count() + status.Missing.Count() +
                status.Staged.Count() + status.Removed.Count() + status.Added.Count() +
                status.Untracked.Count() + status.RenamedInIndex.Count() + status.RenamedInWorkDir.Count();
        }
        catch (Exception exception)
        {
            LogRepositoryException(exception, $"Exception retrieving status for {WorkingDirectory}");
        }
    }

    bool TryRefreshCliStatus(Repository repository, Func<Repository, string[], GitCliResult>? runGit)
    {
        var result = (runGit ?? GitCli.RunStatus)(repository,
        [
            "--no-optional-locks", "status", "--porcelain=v2", "-z", "--untracked-files=all",
            "--ignore-submodules=none"
        ]);
        if (result.Succeeded)
        {
            try
            {
                var status = RepositoryPendingChangesStatus.Parse(result.Output, repository.Info.WorkingDirectory);
                HasPendingChanges = status.IsDirty;
                NumberOfPendingChanges = status.Count;
                return true;
            }
            catch (FormatException)
            {
                // Invalid output is not a clean status; retain the libgit2 fallback.
            }
        }

        if (!_statusFallbackLogged)
        {
            _statusFallbackLogged = true;
            GitWizardLog.Log($"Git CLI status unavailable for {WorkingDirectory}; falling back to libgit2",
                GitWizardLog.LogType.Warning);
        }

        return false;
    }
}
