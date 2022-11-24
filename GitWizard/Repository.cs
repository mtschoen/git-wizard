using LibGit2Sharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace GitWizard;

[Serializable]
public class Repository
{
    public string? WorkingDirectory { get; private set; }
    public string? CurrentBranch { get; private set; }
    public bool IsDetachedHead { get; private set; }
    public bool HasPendingChanges { get; private set; }
    public SortedDictionary<string, Repository?>? Submodules { get; private set; }

    public bool IsRefreshing { get; private set; }

    Repository() { }

    public Repository(string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
    }

    public void Refresh(IUpdateHandler? updateHandler)
    {
        if (string.IsNullOrEmpty(WorkingDirectory) || !Directory.Exists(WorkingDirectory))
        {
            GitWizardLog.Log($"Working directory {WorkingDirectory} is invalid.", GitWizardLog.LogType.Error);
            return;
        }

        GitWizardLog.Log($"Refreshing {WorkingDirectory}");

        try
        {
            var repository = new LibGit2Sharp.Repository(WorkingDirectory);
            IsRefreshing = true;
            Parallel.ForEach(repository.Submodules, submodule =>
            {
                Submodules ??= new SortedDictionary<string, Repository?>();

                var path = Path.Combine(WorkingDirectory, submodule.Path);
                if (!Submodules.TryGetValue(path, out var submoduleRepository))
                {
                    if (submodule.WorkDirCommitId == null)
                    {
                        // Uninitialized submodules will have a null work directory commit id
                        Submodules[path] = null;
                        updateHandler?.OnUninitializedSubmoduleCreated(this, path);
                    }
                    else if (LibGit2Sharp.Repository.IsValid(path))
                    {
                        submoduleRepository = new Repository(path);
                        Submodules[path] = submoduleRepository;
                        updateHandler?.OnSubmoduleCreated(this, submoduleRepository);
                    }
                    else
                    {
                        GitWizardLog.LogException(new Exception(), $"Unknown submodule state for {path}");
                    }
                }

                if (submoduleRepository == null)
                    return;

                submoduleRepository.Refresh(updateHandler);
            });

            CurrentBranch = repository.Head.FriendlyName;
            IsDetachedHead = repository.Head.Reference is not SymbolicReference;
            var status = repository.RetrieveStatus();
            HasPendingChanges = status.IsDirty;

            IsRefreshing = false;
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception thrown trying to refresh {WorkingDirectory}");
        }
    }
}