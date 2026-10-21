using System.Collections.Concurrent;
using GitWizard;

namespace GitWizardTests.TestSupport;

internal sealed class RecordingUpdateHandler : IUpdateHandler
{
    readonly ConcurrentQueue<(string Description, int Total)> _progressStarts = new();
    readonly ConcurrentQueue<int> _progressCounts = new();
    readonly ConcurrentQueue<string?> _messages = new();
    readonly ConcurrentQueue<GitWizardRepository> _repositoriesCreated = new();
    readonly ConcurrentQueue<(GitWizardRepository Parent, GitWizardRepository Submodule)> _submodulesCreated = new();
    readonly ConcurrentQueue<GitWizardRepository> _worktreesCreated = new();
    readonly ConcurrentQueue<(GitWizardRepository Parent, string Path)> _uninitializedSubmodulesCreated = new();
    readonly ConcurrentQueue<GitWizardRepository> _refreshCompleted = new();

    public (string Description, int Total)[] ProgressStarts => _progressStarts.ToArray();
    public int[] ProgressCounts => _progressCounts.ToArray();
    public string?[] Messages => _messages.ToArray();
    public GitWizardRepository[] RepositoriesCreated => _repositoriesCreated.ToArray();
    public (GitWizardRepository Parent, GitWizardRepository Submodule)[] SubmodulesCreated => _submodulesCreated.ToArray();
    public GitWizardRepository[] WorktreesCreated => _worktreesCreated.ToArray();
    public (GitWizardRepository Parent, string Path)[] UninitializedSubmodulesCreated => _uninitializedSubmodulesCreated.ToArray();
    public GitWizardRepository[] RefreshCompleted => _refreshCompleted.ToArray();

    public Action<string, int>? StartingProgress { get; init; }
    public Action<int>? UpdatingProgress { get; init; }
    public Action<string?>? SendingMessage { get; init; }
    public Action<GitWizardRepository>? CreatingRepository { get; init; }
    public Action<GitWizardRepository, GitWizardRepository>? CreatingSubmodule { get; init; }
    public Action<GitWizardRepository>? CreatingWorktree { get; init; }
    public Action<GitWizardRepository, string>? CreatingUninitializedSubmodule { get; init; }
    public Action<GitWizardRepository>? CompletingRefresh { get; init; }

    public void SendUpdateMessage(string? message)
    {
        _messages.Enqueue(message);
        SendingMessage?.Invoke(message);
    }

    public void OnRepositoryCreated(GitWizardRepository gitWizardRepository)
    {
        _repositoriesCreated.Enqueue(gitWizardRepository);
        CreatingRepository?.Invoke(gitWizardRepository);
    }

    public void StartProgress(string description, int total)
    {
        _progressStarts.Enqueue((description, total));
        StartingProgress?.Invoke(description, total);
    }

    public void UpdateProgress(int count)
    {
        _progressCounts.Enqueue(count);
        UpdatingProgress?.Invoke(count);
    }

    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule)
    {
        _submodulesCreated.Enqueue((parent, submodule));
        CreatingSubmodule?.Invoke(parent, submodule);
    }

    public void OnWorktreeCreated(GitWizardRepository worktree)
    {
        _worktreesCreated.Enqueue(worktree);
        CreatingWorktree?.Invoke(worktree);
    }

    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath)
    {
        _uninitializedSubmodulesCreated.Enqueue((parent, submodulePath));
        CreatingUninitializedSubmodule?.Invoke(parent, submodulePath);
    }

    public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository)
    {
        _refreshCompleted.Enqueue(gitWizardRepository);
        CompletingRefresh?.Invoke(gitWizardRepository);
    }
}
