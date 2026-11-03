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

        RefreshError ??= exception.Message;
        _refreshOutcome = RepositoryRefreshOutcome.TransientFailure;
        GitWizardLog.LogException(exception, message);
    }
}
