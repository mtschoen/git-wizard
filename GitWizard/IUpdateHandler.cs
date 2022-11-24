namespace GitWizard;

public interface IUpdateHandler
{
    void SendUpdateMessage(string? message);
    void OnRepositoryCreated(Repository repository);
    void StartProgress(string description, int total);
    void UpdateProgress(int count);
    void OnSubmoduleCreated(Repository parent, Repository submodule);
    void OnUninitializedSubmoduleCreated(Repository parent, string submodulePath);
}