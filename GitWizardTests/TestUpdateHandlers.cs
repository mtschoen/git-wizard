using GitWizard;

namespace GitWizardTests;

/// <summary>
/// IUpdateHandler that records how many times the refresh-completed callback fired. Used to verify
/// that Refresh (and its MarkRefreshFailed error path) notifies the handler exactly once.
/// </summary>
public class CountingUpdateHandler : IUpdateHandler
{
    public int RefreshCompletedCount { get; private set; }

    public void StartProgress(string description, int total) { }
    public void UpdateProgress(int count) { }
    public void SendUpdateMessage(string? message) { }
    public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
    public void OnWorktreeCreated(GitWizardRepository worktree) { }
    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
    public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) => RefreshCompletedCount++;
}

/// <summary>
/// IUpdateHandler that throws from the refresh-completed callback. Used to verify Refresh swallows a
/// throwing completion callback (so one bad handler can't crash a whole scan).
/// </summary>
public class FailingUpdateHandler : IUpdateHandler
{
    public void StartProgress(string description, int total) { }
    public void UpdateProgress(int count) { }
    public void SendUpdateMessage(string? message) { }
    public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
    public void OnWorktreeCreated(GitWizardRepository worktree) { }
    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
    public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) => throw new InvalidOperationException("callback failure");
}
