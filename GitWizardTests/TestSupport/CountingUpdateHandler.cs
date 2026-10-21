using GitWizard;

namespace GitWizardTests;

/// <summary>
/// IUpdateHandler that records how many times the refresh-completed callback fired and which
/// repository it carried. Used to verify that Refresh (and its MarkRefreshFailed error path)
/// notifies the handler exactly once with the refreshed repository instance.
/// </summary>
public class CountingUpdateHandler : IUpdateHandler
{
    int _refreshCompletedCount;

    public int RefreshCompletedCount => _refreshCompletedCount;

    public GitWizardRepository? LastCompletedRepository { get; private set; }

    public void StartProgress(string description, int total) { }
    public void UpdateProgress(int count) { }
    public void SendUpdateMessage(string? message) { }
    public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
    public void OnWorktreeCreated(GitWizardRepository worktree) { }
    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
    public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository)
    {
        Interlocked.Increment(ref _refreshCompletedCount);
        LastCompletedRepository = gitWizardRepository;
    }
}
