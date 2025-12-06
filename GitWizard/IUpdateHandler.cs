namespace GitWizard;

public interface IUpdateHandler
{
    void SendUpdateMessage(string? message);
    void OnRepositoryCreated(GitWizardRepository gitWizardRepository);
    void StartProgress(string description, int total);
    void UpdateProgress(int count);
    void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule);
    void OnWorktreeCreated(GitWizardRepository worktree);
    void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath);
    void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository);
}