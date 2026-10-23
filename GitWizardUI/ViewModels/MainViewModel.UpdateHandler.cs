using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    string? _progressDescription;

    // Plain int fields instead of int? so the UI drain (MainViewModel.UiDrain.cs) can never
    // observe a torn read across the HasValue/Value pair of a two-field Nullable<int> struct
    // written from a worker thread while read from the UI thread. _progressActive replaces the
    // former HasValue sentinel; volatile gives both the field write in StartProgress/UpdateProgress
    // and the field read on the UI drain the memory-visibility guarantee a plain field lacks.
    volatile bool _progressActive;
    int _progressTotal;
    int _progressCount;

    // Coalesce a burst of status messages into one UI update. A heavy scan (the
    // recursive walk posts per directory) or refresh can fire thousands of these;
    // posting each one floods the dispatcher and starves UI input ("Not Responding").
    // Instead we keep only the latest message and allow at most one post in flight -
    // many updates batch into a single StatusHeader.HeaderText write, event-driven (no polling).
    string? _latestStatusMessage;
    int _statusUpdateScheduled;

    public void SendUpdateMessage(string? message)
    {
        if (message == null)
        {
            return;
        }

        _latestStatusMessage = message;

        // Schedule a post only if none is pending; the pending post applies whatever
        // the most recent message is by the time it runs on the UI thread.
        if (Interlocked.CompareExchange(ref _statusUpdateScheduled, 1, 0) == 0)
        {
            _ui.Post(() =>
            {
                Interlocked.Exchange(ref _statusUpdateScheduled, 0);
                StatusHeader.ShowRefreshMessage(_latestStatusMessage);
            });
        }
    }

    public void OnRepositoryCreated(GitWizardRepository gitWizardRepository)
    {
        EnqueueUICommand(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.RepositoryCreated,
            Repository = gitWizardRepository
        });
    }

    public void StartProgress(string description, int total)
    {
        _progressCount = 0;
        _progressDescription = description;
        _progressTotal = total;
        _progressActive = true;
        _ui.Post(() =>
        {
            IsScanning = false;
            IsProgressVisible = true;
            ProgressValue = 0;
            ProgressText = $"{description} 0 / {total}";
        });
    }

    public void UpdateProgress(int count)
    {
        _progressCount = count;
        ScheduleUIDrain();
    }

    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule)
    {
        EnqueueUICommand(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.SubmoduleCreated,
            ParentRepository = parent,
            Repository = submodule
        });
    }

    public void OnWorktreeCreated(GitWizardRepository worktree)
    {
        EnqueueUICommand(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.WorktreeCreated,
            Repository = worktree
        });
    }

    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath)
    {
        EnqueueUICommand(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.UninitializedSubmoduleCreated,
            ParentRepository = parent,
            SubmodulePath = submodulePath
        });
    }

    public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository)
    {
        EnqueueUICommand(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.RefreshCompleted,
            Repository = gitWizardRepository
        });
    }

    enum RepositoryUICommandType
    {
        RepositoryCreated = 0,
        SubmoduleCreated = 1,
        WorktreeCreated = 2,
        UninitializedSubmoduleCreated = 3,
        RefreshCompleted = 4
    }

    struct RepositoryUICommand
    {
        public RepositoryUICommandType Type;
        public GitWizardRepository? Repository;
        public GitWizardRepository? ParentRepository;
        public string? SubmodulePath;
    }
}
