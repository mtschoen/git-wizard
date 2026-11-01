using LibGit2Sharp;

namespace GitWizard;

public partial class GitWizardRepository
{
    internal void LogRepositoryException(Exception exception, string message)
    {
        if (IsOwnershipBlocked(exception))
        {
            _refreshOutcome = RepositoryRefreshOutcome.OwnershipBlocked;
            RefreshError = OwnershipBlockedError;
            GitWizardLog.Log($"{OwnershipBlockedError}: {WorkingDirectory}", GitWizardLog.LogType.Warning);
            return;
        }

        if (exception is NotFoundException ||
            exception is LibGit2SharpException &&
            exception.Message.Contains("failed to read index", StringComparison.OrdinalIgnoreCase))
        {
            _refreshOutcome = RepositoryRefreshOutcome.NonRepository;
        }

        GitWizardLog.LogException(exception, message);
    }
}
