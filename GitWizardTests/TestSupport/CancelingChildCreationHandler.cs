using GitWizard;

namespace GitWizardTests;

/// <summary>
/// IUpdateHandler that cancels a shared CancellationTokenSource from inside the
/// OnSubmoduleCreated / OnWorktreeCreated callback - i.e. from inside the Parallel.ForEach loop
/// in RefreshSubmodules / RefreshWorktrees, after that phase's own ThrowIfCancellationRequested
/// guard has already let the phase begin. Used to prove cancellation genuinely propagates into a
/// running submodule/worktree refresh, rather than being cancelled at the phase boundary before
/// the phase is ever entered (see RefreshPhaseObservationAndCancellationTests). Records the created child repository
/// so the caller can assert it also stopped.
/// </summary>
public class CancelingChildCreationHandler : IUpdateHandler
{
    readonly CancellationTokenSource _cancellationTokenSource;

    public int SubmoduleCreatedCalls { get; private set; }
    public int WorktreeCreatedCalls { get; private set; }
    public GitWizardRepository? CreatedSubmodule { get; private set; }
    public GitWizardRepository? CreatedWorktree { get; private set; }

    public CancelingChildCreationHandler(CancellationTokenSource cancellationTokenSource) => _cancellationTokenSource = cancellationTokenSource;

    public void StartProgress(string description, int total) { }
    public void UpdateProgress(int count) { }
    public void SendUpdateMessage(string? message) { }
    public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }

    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule)
    {
        SubmoduleCreatedCalls++;
        CreatedSubmodule = submodule;
        _cancellationTokenSource.Cancel();
    }

    public void OnWorktreeCreated(GitWizardRepository worktree)
    {
        WorktreeCreatedCalls++;
        CreatedWorktree = worktree;
        _cancellationTokenSource.Cancel();
    }

    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
    public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
}
