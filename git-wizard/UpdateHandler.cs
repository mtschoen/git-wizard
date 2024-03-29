﻿namespace GitWizard.CLI;

class UpdateHandler : IUpdateHandler
{
    public void SendUpdateMessage(string? message)
    {
        if (message == null)
        {
            GitWizardLog.LogException(new ArgumentException("Tried to log a null message", nameof(message)));
            return;
        }

        GitWizardLog.Log(message, GitWizardLog.LogType.Verbose);
    }

    public void OnRepositoryCreated(Repository repository)
    {
        var workingDirectory = repository.WorkingDirectory;
        if (workingDirectory == null)
        {
            GitWizardLog.LogException(new ArgumentException("Created a repository with null working directory", nameof(workingDirectory)));
            return;
        }

        GitWizardLog.Log(workingDirectory, GitWizardLog.LogType.Verbose);
    }

    public void StartProgress(string description, int total)
    {
        // TODO: Persistent progress bar
    }

    public void UpdateProgress(int count)
    {
        // TODO: Persistent progress bar
    }

    public void OnSubmoduleCreated(Repository parent, Repository submodule)
    {
        // Nothing to do here
    }

    public void OnWorktreeCreated(Repository worktrees)
    {
        // Nothing to do here
    }

    public void OnUninitializedSubmoduleCreated(Repository parent, string submodulePath)
    {
        // Nothing to do here
    }

    public void OnRepositoryRefreshCompleted(Repository repository)
    {
        // Nothing to do here
    }
}