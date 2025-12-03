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
    public int NumberOfPendingChanges { get; private set; }
    public bool IsWorktree { get; private set; }
    public SortedDictionary<string, Repository?>? Submodules { get; private set; }
    public SortedDictionary<string, Repository?>? Worktrees { get; private set; }

    public bool IsRefreshing { get; private set; }
    public bool LocalOnlyCommits { get; private set; }

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

            try
            {
                // Refresh the index to update stale stat data
                RefreshIndex(repository);

                var status = repository.RetrieveStatus();
                HasPendingChanges = status.IsDirty;
                if (HasPendingChanges)
                {
                    NumberOfPendingChanges = 0;
                    foreach (var _ in status.Modified)
                    {
                        NumberOfPendingChanges++;
                    }

                    foreach (var _ in status.Staged)
                    {
                        NumberOfPendingChanges++;
                    }

                    foreach (var _ in status.Removed)
                    {
                        NumberOfPendingChanges++;
                    }
                }
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, $"Exception retrieving status for {WorkingDirectory}");
            }

            // TODO: Enable/disable deep checks
            // TODO: Include optional fetch
            foreach (var branch in repository.Branches)
            {
                // Check if this is a local branch (not a remote tracking branch)
                if (branch.IsRemote)
                    continue;

                // Case 1: Local branch not tracking any remote
                if (branch.TrackedBranch == null)
                {
                    LocalOnlyCommits = true;
                    continue;
                }

                // Case 2: Local branch is ahead of its remote tracking branch
                if (branch.Tip != null && branch.TrackedBranch.Tip != null && branch.Tip != branch.TrackedBranch.Tip)
                {
                    var divergence = repository.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, branch.TrackedBranch.Tip);
                    if (divergence.AheadBy > 0)
                    {
                        LocalOnlyCommits = true;
                    }
                }
            }

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
        try
        {
            var submodules = repository.Submodules;
            if (!submodules.Any())
                return;

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
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception enumerating submodules for {WorkingDirectory}");
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
                string? path = null;
                Repository? worktreeRepository = null;

                try
                {
                    path = worktree.WorktreeRepository.Info.WorkingDirectory;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        path = path.ToLowerInvariant();

                    bool hasExisting;
                    lock (Worktrees)
                    {
                        hasExisting = Worktrees.TryGetValue(path, out worktreeRepository);
                    }

                    if (!hasExisting)
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
                                    "Exception thrown by Refresh OnWorktreeCreated callback.");
                            }
                        }
                        else
                        {
                            GitWizardLog.LogException(new Exception(), $"Unknown worktree state for {path}");
                        }
                    }

                    if (worktreeRepository != null)
                    {
                        worktreeRepository.Refresh(updateHandler);
                    }
                }
                catch (Exception exception)
                {
                    GitWizardLog.LogException(exception,
                        $"Exception updating worktrees for {WorkingDirectory}");
                }
            });
        }
    }

    static void RefreshIndex(LibGit2Sharp.Repository repository)
    {
        try
        {
            // Call git update-index --refresh to update stale stat cache
            // This matches what 'git status' does automatically
            // TODO: Remove this workaround once LibGit2Sharp exposes GIT_STATUS_OPT_UPDATE_INDEX
            // See: https://github.com/libgit2/libgit2/discussions/6852
            var workingDirectory = repository.Info.WorkingDirectory;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "update-index --refresh -q",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = System.Diagnostics.Process.Start(psi))
            {
                if (process != null)
                {
                    process.WaitForExit();

                    // Note: git update-index --refresh returns non-zero if files are truly modified,
                    // which is normal and expected. We only log if there's an actual error message.
                    if (process.ExitCode != 0)
                    {
                        var error = process.StandardError.ReadToEnd();
                        if (!string.IsNullOrEmpty(error))
                        {
                            GitWizardLog.Log($"git update-index --refresh reported: {error}", GitWizardLog.LogType.Warning);
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception refreshing index via git CLI");
        }
    }
}