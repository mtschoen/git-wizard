namespace GitWizard;

public interface IUpdateHandler
{
    void SendUpdateMessage(string? message);
    void OnRepositoryCreated(Repository repository);
}