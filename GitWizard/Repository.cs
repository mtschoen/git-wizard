using LibGit2Sharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public void Refresh(IUpdateHandler? updateHandler = null)
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
            if (repository.Submodules.Any())
            {
                Submodules = new SortedDictionary<string, Repository?>();
                Parallel.ForEach(repository.Submodules, submodule =>
                {
                    var path = Path.Combine(WorkingDirectory, submodule.Path);
                    Repository? submoduleRepository;
                    bool hasExisting;
                    lock (Submodules)
                    {
                        hasExisting = Submodules.TryGetValue(path, out submoduleRepository);
                    }

                    if (!hasExisting)
                    {
                        if (submodule.WorkDirCommitId == null)
                        {
                            // Uninitialized submodules will have a null work directory commit id
                            lock (Submodules)
                            {
                                Submodules[path] = null;
                            }

                            updateHandler?.OnUninitializedSubmoduleCreated(this, path);
                        }
                        else if (LibGit2Sharp.Repository.IsValid(path))
                        {
                            submoduleRepository = new Repository(path);
                            lock (Submodules)
                            {
                                Submodules[path] = submoduleRepository;
                            }

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
            }

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