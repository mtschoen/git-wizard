using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using GitWizard;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged, IUpdateHandler
{
    readonly IUiDispatcher _ui;
    readonly IUserDialogs _dialogs;
    readonly IClipboardService _clipboard;
    readonly ConcurrentDictionary<string, RepositoryNodeViewModel> _repositoryMap = new();
    readonly ConcurrentQueue<RepositoryUICommand> _uiCommands = new();
    readonly Stopwatch _stopwatch = new();
    readonly List<RepositoryNodeViewModel> _allRepositories = new();
    readonly Dictionary<string, RepositoryNodeViewModel> _pendingGroups = new();

    string _headerText = "GitWizard";
    double _progressValue;
    bool _isProgressVisible;
    string _progressText = string.Empty;
    bool _isRefreshing;
    bool _isLive;
    bool _isScanning;
    FilterType _activeFilter = FilterType.None;
    GroupMode _activeGroupMode = GroupMode.None;
    SortMode _activeSortMode = SortMode.WorkingDirectory;
    string? _lastRefreshMessage;
    const int SearchDebounceMilliseconds = 200;
    CancellationTokenSource? _searchDebounceCts;
    string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                OnPropertyChanged();
                DebounceSearch();
            }
        }
    }
    public static string? GlobalUserEmail { get; private set; }

    string? _progressDescription;
    int? _progressTotal;
    int? _progressCount;

    ObservableCollection<RepositoryNodeViewModel> _repositories = new();

    public ObservableCollection<RepositoryNodeViewModel> Repositories
    {
        get => _repositories;
        private set
        {
            if (_repositories != value)
            {
                _repositories = value;
                OnPropertyChanged();
            }
        }
    }

    public string HeaderText
    {
        get => _headerText;
        set
        {
            if (_headerText != value)
            {
                _headerText = value;
                OnPropertyChanged();
            }
        }
    }

    public double ProgressValue
    {
        get => _progressValue;
        set
        {
            if (Math.Abs(_progressValue - value) > 0.001)
            {
                _progressValue = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        set
        {
            if (_isProgressVisible != value)
            {
                _isProgressVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public string ProgressText
    {
        get => _progressText;
        set
        {
            if (_progressText != value)
            {
                _progressText = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set
        {
            if (_isRefreshing != value)
            {
                _isRefreshing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanRefresh));
                OnPropertyChanged(nameof(CanToggleLive));
            }
        }
    }

    public bool CanRefresh => !IsRefreshing;

    /// <summary>
    /// True while a <see cref="GitWizardUI.Services.LiveWatchController"/> session is running (elevated USN-journal
    /// watch on Windows), auto-updating the repository list as tracked/search-root repos
    /// change on disk. Toggled via <see cref="ToggleLiveCommand"/>.
    /// </summary>
    public bool IsLive
    {
        get => _isLive;
        internal set
        {
            if (_isLive != value)
            {
                _isLive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanToggleLive));
            }
        }
    }

    /// <summary>
    /// Live mode may not be started mid-refresh: a full scan rebuilds <c>_repositoryMap</c>/
    /// <c>_allRepositories</c> from scratch, which would race a live event's incremental
    /// add/update/remove against that rebuild. Stopping is always allowed.
    /// </summary>
    public bool CanToggleLive => IsLive || !IsRefreshing;

    /// <summary>
    /// True from the moment a refresh starts until the first repository surfaces (or the
    /// determinate progress bar takes over). Drives an indeterminate "Scanning…" indicator that
    /// fills the otherwise-empty list during the initial discovery/cache-read gap, which can run
    /// 15-20s on a cold start. Parity with the retired MAUI UI's startup feedback.
    /// </summary>
    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (_isScanning != value)
            {
                _isScanning = value;
                OnPropertyChanged();
            }
        }
    }

    public FilterType ActiveFilter
    {
        get => _activeFilter;
        private set
        {
            if (_activeFilter != value)
            {
                _activeFilter = value;
                OnPropertyChanged();
            }
        }
    }

    public GroupMode ActiveGroupMode
    {
        get => _activeGroupMode;
        private set
        {
            if (_activeGroupMode != value)
            {
                _activeGroupMode = value;
                OnPropertyChanged();
            }
        }
    }

    public SortMode ActiveSortMode
    {
        get => _activeSortMode;
        private set
        {
            if (_activeSortMode != value)
            {
                _activeSortMode = value;
                OnPropertyChanged();
            }
        }
    }

    public Action<RepositoryNodeViewModel>? ScrollToRequest { get; set; }

    /// <summary>
    /// Optional view hook invoked (on the UI thread) immediately after the <see cref="Repositories"/>
    /// collection is swapped, so a desktop shell can restore the user's scroll position after a
    /// refresh rebuilds the list. Avalonia's ListBox resets scroll to the top when its ItemsSource
    /// is replaced (AvaloniaUI/Avalonia#5651) and has no KeepScrollOffset equivalent; MAUI preserves
    /// offset declaratively and leaves this null.
    /// </summary>
    public Action? AfterRepositoriesSwap { get; set; }
    public ICommand OpenInExplorerCommand { get; }
    public ICommand OpenInForkCommand { get; }
    public ICommand CopyToClipboardCommand { get; }
    public ICommand DeepRefreshCommand { get; }
    public ICommand CheckoutMatchingBranchCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand FetchAndRefreshCommand { get; }
    public ICommand CleanDownstreamCommand { get; }
    public ICommand ToggleLiveCommand { get; }

    public MainViewModel(IUiDispatcher ui, IUserDialogs dialogs, IClipboardService clipboard)
    {
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        OpenInExplorerCommand = new RelayCommand<RepositoryNodeViewModel>(OpenInExplorer);
        OpenInForkCommand = new RelayCommand<RepositoryNodeViewModel>(OpenInFork);
        CopyToClipboardCommand = new AsyncRelayCommand<RepositoryNodeViewModel>(CopyToClipboardAsync);
        DeepRefreshCommand = new RelayCommand<RepositoryNodeViewModel>(DeepRefreshRepository);
        CheckoutMatchingBranchCommand = new RelayCommand<RepositoryNodeViewModel>(CheckoutMatchingBranch);
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(background: false));
        FetchAndRefreshCommand = new AsyncRelayCommand(() => RefreshAsync(background: false, fetchRemotes: true));
        CleanDownstreamCommand = new AsyncRelayCommand<RepositoryNodeViewModel>(node => CleanDownstreamBranchesAsync(node));
        ToggleLiveCommand = new AsyncRelayCommand(ToggleLiveAsync, () => CanToggleLive);
        StartUIUpdateThread();
    }

    /// <summary>
    /// Marshal a user-facing alert onto the UI thread, logging (not propagating) any dialog
    /// failure so a fire-and-forget alert can't crash the process.
    /// </summary>
    void ShowAlert(string title, string message)
        => _ui.Post(() => _ = DisplayAlertSafeAsync(title, message));

    async Task DisplayAlertSafeAsync(string title, string message)
    {
        try
        {
            await _dialogs.DisplayAlertAsync(title, message);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Failed to display alert dialog.");
        }
    }

    // IUpdateHandler implementation

    // Coalesce a burst of status messages into one UI update. A heavy scan (the
    // recursive walk posts per directory) or refresh can fire thousands of these;
    // posting each one floods the dispatcher and starves UI input ("Not Responding").
    // Instead we keep only the latest message and allow at most one post in flight -
    // many updates batch into a single HeaderText write, event-driven (no polling).
    string? _latestStatusMessage;
    int _statusUpdateScheduled;

    public void SendUpdateMessage(string? message)
    {
        if (message == null)
            return;

        _latestStatusMessage = message;

        // Schedule a post only if none is pending; the pending post applies whatever
        // the most recent message is by the time it runs on the UI thread.
        if (Interlocked.CompareExchange(ref _statusUpdateScheduled, 1, 0) == 0)
        {
            _ui.Post(() =>
            {
                Interlocked.Exchange(ref _statusUpdateScheduled, 0);
                HeaderText = _latestStatusMessage;
            });
        }
    }

    public void OnRepositoryCreated(GitWizardRepository gitWizardRepository)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
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
    }

    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.SubmoduleCreated,
            ParentRepository = parent,
            Repository = submodule
        });
    }

    public void OnWorktreeCreated(GitWizardRepository worktree)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.WorktreeCreated,
            Repository = worktree
        });
    }

    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.UninitializedSubmoduleCreated,
            ParentRepository = parent,
            SubmodulePath = submodulePath
        });
    }

    public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.RefreshCompleted,
            Repository = gitWizardRepository
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    enum RepositoryUICommandType
    {
        RepositoryCreated,
        SubmoduleCreated,
        WorktreeCreated,
        UninitializedSubmoduleCreated,
        RefreshCompleted
    }

    struct RepositoryUICommand
    {
        public RepositoryUICommandType Type;
        public GitWizardRepository? Repository;
        public GitWizardRepository? ParentRepository;
        public string? SubmodulePath;
    }
}
