using System.Collections.Concurrent;
using GitWizard;

namespace GitWizardUI.ViewModels;

public sealed partial class RefreshCoordinator
{
    // Event-driven drain: the first enqueue (or progress update) of a burst starts one batching
    // window on the injected TimeProvider; the window's expiry posts exactly one drain to the UI
    // dispatcher, which empties the command queue and applies the latest progress. Arrivals inside
    // the window ride the pending drain, preserving the coalescing described on DrainUICommands.
    // MainViewModel.DisposeAsync stops the window and turns a drain that was already posted into a no-op.
    static readonly TimeSpan UiDrainBatchingWindow = TimeSpan.FromMilliseconds(250);

    static readonly TimeSpan UiDrainPollInterval = TimeSpan.FromMilliseconds(150);

    readonly ConcurrentQueue<RepositoryUICommand> _uiCommands = new();
    readonly Lock _uiDrainLock = new();
    ITimer? _uiDrainWindow;
    int _uiDrainScheduled;
    volatile bool _uiDrainDisposed;
    readonly CancellationTokenSource _uiDrainShutdown = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);

    string? _progressDescription;

    // Plain int fields instead of int? so the UI drain (RefreshCoordinator.UiDrain.cs) can never
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
                _statusHeader.ShowRefreshMessage(_latestStatusMessage);
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

    /// <summary>
    /// Queues a command for the next batched drain. Shutdown contract: once disposal has begun the
    /// queue is closed. Commands still pending at that moment are discarded with the view model,
    /// and a command enqueued afterwards is dropped without throwing into the producer. Nothing
    /// can be applied after disposal, so a dropped command is never observable as lost work.
    /// </summary>
    void EnqueueUICommand(RepositoryUICommand command)
    {
        if (_uiDrainDisposed)
        {
            return;
        }

        _uiCommands.Enqueue(command);

        // Re-check after enqueueing: disposal that began between the first check and the enqueue
        // may have cleared the queue before this command landed, so this producer clears it again.
        if (_uiDrainDisposed)
        {
            _uiCommands.Clear();
            return;
        }

        ScheduleUIDrain();
    }

    /// <summary>
    /// Waits until the command queue is empty. Returns false when disposal ended the wait, so the
    /// caller abandons the refresh instead of publishing into a disposed view model.
    /// </summary>
    internal async Task<bool> WaitForUICommandsToDrainAsync()
    {
        var shutdown = _uiDrainShutdown.Token;
        while (!_uiCommands.IsEmpty)
        {
            try
            {
                await Task.Delay(UiDrainPollInterval, _timeProvider, shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return !_uiDrainDisposed;
    }

    void ScheduleUIDrain()
    {
        if (Interlocked.CompareExchange(ref _uiDrainScheduled, 1, 0) != 0)
        {
            return;
        }

        lock (_uiDrainLock)
        {
            if (_uiDrainDisposed)
            {
                return;
            }

            _uiDrainWindow = _timeProvider.CreateTimer(OnUIDrainWindowElapsed, null,
                UiDrainBatchingWindow, Timeout.InfiniteTimeSpan);
        }
    }

    void OnUIDrainWindowElapsed(object? state)
    {
        lock (_uiDrainLock)
        {
            if (_uiDrainDisposed)
            {
                return;
            }

            _uiDrainWindow?.Dispose();
            _uiDrainWindow = null;
        }

        _ui.Post(RunScheduledUIDrain);
    }

    void RunScheduledUIDrain()
    {
        if (_uiDrainDisposed)
        {
            return;
        }

        // Reset before draining so an arrival during the drain starts the next window.
        Interlocked.Exchange(ref _uiDrainScheduled, 0);

        // Drain all pending commands in one UI dispatch to minimize layout passes
        DrainUICommands();
        ApplyProgress();
    }

    // Drains the UI command queue in one pass. Every RepositoryCreated/WorktreeCreated command in
    // the drained slice is collected into a single batch and applied with one AddRepositoriesBatch
    // call; every other command is staged and processed afterwards, in arrival order. A refresh
    // worker enqueues RepositoryCreated, refreshes, then enqueues RefreshCompleted, so with
    // parallel workers the queue alternates create/complete after the initial burst. Flushing the
    // pending batch at each interleaved completion (the earlier drain) sent each resulting one-row
    // batch back through AddRepository's per-row insertion, which shifts up to the whole displayed
    // list on every call: O(n^2) element movement across n interleaved arrivals (issue #245,
    // round-3 pr-crew review of PR #241/#243). Coalescing across the whole slice ensures each drain
    // merges its creates in a single sorted pass rather than repeated flushes. Processing the
    // staged commands after the batch is safe for ordering: a repository's create command is always
    // enqueued before its own completion, so its row exists by the time the completion is applied.
    // Internal so tests can drive the real queue/drain interaction synchronously.
    internal void DrainUICommands()
    {
        List<GitWizardRepository>? pendingRepositoryBatch = null;
        List<RepositoryUICommand>? stagedCommands = null;

        while (_uiCommands.TryDequeue(out var command))
        {
            if (command is { Type: RepositoryUICommandType.RepositoryCreated or RepositoryUICommandType.WorktreeCreated, Repository: not null })
            {
                pendingRepositoryBatch ??= [];
                pendingRepositoryBatch.Add(command.Repository);
                continue;
            }

            stagedCommands ??= [];
            stagedCommands.Add(command);
        }

        if (pendingRepositoryBatch != null)
        {
            // The first repository to surface ends the "Scanning..." gap; rows now stream into the list.
            IsScanning = false;
            _repositoryList.AddRepositoriesBatch(pendingRepositoryBatch, streaming: IsRefreshing);
        }

        if (stagedCommands == null)
        {
            return;
        }

        foreach (var command in stagedCommands)
        {
            ProcessUICommand(command);
        }
    }

    void ProcessUICommand(RepositoryUICommand command)
    {
        switch (command.Type)
        {
            case RepositoryUICommandType.RepositoryCreated:
                if (command.Repository != null)
                {
                    IsScanning = false;
                    _repositoryList.AddRepository(command.Repository, streaming: IsRefreshing);
                }

                break;
            case RepositoryUICommandType.SubmoduleCreated:
                if (command.ParentRepository != null && command.Repository != null)
                {
                    _repositoryList.AddSubmodule(command.ParentRepository, command.Repository);
                }

                break;
            case RepositoryUICommandType.WorktreeCreated:
                if (command.Repository != null)
                {
                    IsScanning = false;
                    _repositoryList.AddRepository(command.Repository, streaming: IsRefreshing);
                }

                break;
            case RepositoryUICommandType.UninitializedSubmoduleCreated:
                if (command.ParentRepository != null && command.SubmodulePath != null)
                {
                    RepositoryListViewModel.AddUninitializedSubmodule(command.ParentRepository, command.SubmodulePath);
                }

                break;
            case RepositoryUICommandType.RefreshCompleted:
                if (command.Repository != null)
                {
                    _repositoryList.UpdateCompletedRepository(command.Repository, streaming: IsRefreshing);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Type, "Unknown repository UI command type.");
        }
    }

    // Called by MainViewModel.DisposeAsync before stopping Live watch so no queued refresh
    // commands or batching windows fire after disposal begins.
    internal void StopUIDrain()
    {
        lock (_uiDrainLock)
        {
            _uiDrainDisposed = true;
            _uiDrainWindow?.Dispose();
            _uiDrainWindow = null;
        }

        // Ends any refresh waiting on the queue, then discards what no drain will ever apply.
        _uiDrainShutdown.Cancel();
        _uiCommands.Clear();
    }

    void ApplyProgress()
    {
        if (!_progressActive || _progressTotal <= 0)
        {
            return;
        }

        ProgressValue = (double)_progressCount / _progressTotal;
        ProgressText = $"{_progressDescription} {_progressCount} / {_progressTotal}";
        IsProgressVisible = true;

        if (_progressTotal == _progressCount)
        {
            IsProgressVisible = false;
        }
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
