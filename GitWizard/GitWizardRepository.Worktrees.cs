using System.Runtime.InteropServices;
using LibGit2Sharp;

namespace GitWizard;

public partial class GitWizardRepository
{
    void RefreshWorktrees(IUpdateHandler? updateHandler, Repository repository, CancellationToken cancellationToken)
    {
        var worktrees = repository.Worktrees;
        if (!worktrees.Any())
        {
            return;
        }

        Worktrees ??= new SortedDictionary<string, GitWizardRepository?>();
        Parallel.ForEach(worktrees, new ParallelOptions { CancellationToken = cancellationToken }, worktree =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (worktree == null)
                {
                    GitWizardLog.Log($"Worktree is null in {repository}");
                    return;
                }

                var path = worktree.WorktreeRepository.Info.WorkingDirectory;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    path = path.ToLowerInvariant();
                }

                bool hasExisting;
                GitWizardRepository? worktreeRepository;
                lock (Worktrees)
                {
                    hasExisting = Worktrees.TryGetValue(path, out worktreeRepository);
                }

                if (!hasExisting)
                {
                    if (Repository.IsValid(path))
                    {
                        worktreeRepository = new GitWizardRepository(path) { IsWorktree = true };
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
                        GitWizardLog.LogException(new InvalidOperationException("Worktree in unknown state"), $"Unknown worktree state for {path}");
                    }
                }

                worktreeRepository?.Refresh(updateHandler, cancellationToken: cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                GitWizardLog.LogException(exception,
                    $"Exception updating worktrees for {WorkingDirectory}");
            }
        });
    }
}
