using LibGit2Sharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace GitWizard;

[Serializable]
public class Repository
{
    public string? WorkingDirectory { get; private set; }
    public string? CurrentBranch { get; private set; }
    public bool IsDetachedHead { get; private set; }
    public bool HasPendingChanges { get; private set; }
    public bool IsWorktree { get; private set; }
    public SortedDictionary<string, Repository?>? Submodules { get; private set; }
    public SortedDictionary<string, Repository?>? Worktrees { get; private set; }

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
            RefreshSubmodules(updateHandler, repository, WorkingDirectory);

            // Worktrees can't have worktrees, and trying to refresh them will cause an infinite loop
            if (!IsWorktree)
                RefreshWorktrees(updateHandler, repository);

            CurrentBranch = repository.Head.FriendlyName;
            IsDetachedHead = repository.Head.Reference is not SymbolicReference;
            var status = repository.RetrieveStatus();
            HasPendingChanges = status.IsDirty;

            IsRefreshing = false;

            try
            {
                updateHandler?.OnRepositoryRefreshCompleted(this);
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, "Exception thrown by Refresh OnRepositoryRefreshCompleted callback.");
            }
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception thrown trying to refresh {WorkingDirectory}");
        }
    }

    void RefreshSubmodules(IUpdateHandler? updateHandler, LibGit2Sharp.Repository repository, string workingDirectory)
    {
        var submodules = repository.Submodules;
        if (submodules.Any())
        {
            Submodules ??= new SortedDictionary<string, Repository?>();
            Parallel.ForEach(submodules, submodule =>
            {
                var path = Path.Combine(workingDirectory, submodule.Path);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    path = path.ToLowerInvariant();

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

                        try
                        {
                            updateHandler?.OnUninitializedSubmoduleCreated(this, path);
                        }
                        catch (Exception exception)
                        {
                            GitWizardLog.LogException(exception,
                                "Exception thrown by Refresh OnUninitializedSubmoduleCreated callback.");
                        }
                    }
                    else
                    {
                        try
                        {
                            if (LibGit2Sharp.Repository.IsValid(path))
                            {
                                submoduleRepository = new Repository(path);
                                lock (Submodules)
                                {
                                    Submodules[path] = submoduleRepository;
                                }

                                try
                                {
                                    updateHandler?.OnSubmoduleCreated(this, submoduleRepository);
                                }
                                catch (Exception exception)
                                {
                                    GitWizardLog.LogException(exception,
                                        "Exception thrown by Refresh OnSubmoduleCreated callback.");
                                }
                            }
                            else
                            {
                                GitWizardLog.LogException(new Exception(), $"Unknown submodule state for {path}");
                            }
                        }
                        catch (Exception exception)
                        {
                            GitWizardLog.LogException(exception,
                                $"Exception updating submodules for {WorkingDirectory}");
                        }
                    }
                }

                if (submoduleRepository == null)
                    return;

                submoduleRepository.Refresh(updateHandler);
            });
        }
    }

    void RefreshWorktrees(IUpdateHandler? updateHandler, LibGit2Sharp.Repository repository)
    {
        var worktrees = repository.Worktrees;
        if (worktrees.Any())
        {
            Worktrees ??= new SortedDictionary<string, Repository?>();
            Parallel.ForEach(worktrees, worktree =>
            {
                if (worktree == null)
                    return;

                var path = worktree.WorktreeRepository.Info.WorkingDirectory;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    path = path.ToLowerInvariant();

                Repository? worktreeRepository;
                bool hasExisting;
                lock (Worktrees)
                {
                    hasExisting = Worktrees.TryGetValue(path, out worktreeRepository);
                }

                if (!hasExisting)
                {
                    try
                    {
                        if (LibGit2Sharp.Repository.IsValid(path))
                        {
                            worktreeRepository = new Repository(path) {IsWorktree = true};
                            lock (Worktrees)
                            {
                                Worktrees[path] = worktreeRepository;
                            }

                            try
                            {
                                updateHandler?.OnWorktreeCreated(worktreeRepository);
                            }
                            catch (Exception exception)
                            {
                                GitWizardLog.LogException(exception,
                                    "Exception thrown by Refresh OnSubmoduleCreated callback.");
                            }
                        }
                        else
                        {
                            GitWizardLog.LogException(new Exception(), $"Unknown submodule state for {path}");
                        }
                    }
                    catch (Exception exception)
                    {
                        GitWizardLog.LogException(exception,
                            $"Exception updating worktrees for {WorkingDirectory}");
                    }
                }

                if (worktreeRepository == null)
                    return;

                worktreeRepository.Refresh(updateHandler);
            });
        }
    }
}