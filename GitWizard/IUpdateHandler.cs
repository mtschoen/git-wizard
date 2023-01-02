namespace GitWizard;

public interface IUpdateHandler
{
    void SendUpdateMessage(string? message);
    void OnRepositoryCreated(Repository repository);
    void StartProgress(string description, int total);
    void UpdateProgress(int count);
    void OnSubmoduleCreated(Repository parent, Repository submodule);
    void OnWorktreeCreated(Repository worktree);
    void OnUninitializedSubmoduleCreated(Repository parent, string submodulePath);
    void OnRepositoryRefreshCompleted(Repository repository);
}