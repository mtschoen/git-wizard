using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using GitWizard;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged, IUpdateHandler, IAsyncDisposable
{
    readonly IUiDispatcher _ui;
    readonly IUserDialogs _dialogs;
    readonly IClipboardService _clipboard;
    internal readonly ConcurrentDictionary<string, RepositoryNodeViewModel> _repositoryMap = new();
    readonly ConcurrentQueue<RepositoryUICommand> _uiCommands = new();
    HashSet<RepositoryNodeViewModel>? _prePopulatedNodes;
    readonly Stopwatch _stopwatch = new();
    internal readonly List<RepositoryNodeViewModel> _allRepositories = new();
    readonly Dictionary<string, RepositoryNodeViewModel> _pendingGroups = new();

    string _headerText = "GitWizard";
    double _progressValue;
    bool _isProgressVisible;
    string _progressText = string.Empty;
    bool _isRefreshing;
    bool _isLive;
    bool _isLiveStarting;
    bool _isScanning;
    FilterType _activeFilter = FilterType.None;
    GroupMode _activeGroupMode = GroupMode.None;
    SortMode _activeSortMode = SortMode.WorkingDirectory;
    string? _lastRefreshMessage;
    const int SearchDebounceMilliseconds = 200;
    readonly SearchDebouncer _searchDebouncer = new();
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
                OnPropertyChanged(nameof(LiveTooltip));
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
                OnPropertyChanged(nameof(LiveButtonText));
                OnPropertyChanged(nameof(LiveTooltip));
            }
        }
    }

    /// <summary>True while the broker is arming and building its per-volume path indexes.</summary>
    public bool IsLiveStarting
    {
        get => _isLiveStarting;
        internal set
        {
            if (_isLiveStarting != value)
            {
                _isLiveStarting = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanToggleLive));
                OnPropertyChanged(nameof(LiveButtonText));
                OnPropertyChanged(nameof(LiveTooltip));
            }
        }
    }

    public string LiveButtonText => IsLiveStarting ? "Starting Live..." : "Live";

    /// <summary>
    /// Live mode may not be started mid-refresh: a full scan rebuilds <c>_repositoryMap</c>/
    /// <c>_allRepositories</c> from scratch, which would race a live event's incremental
    /// add/update/remove against that rebuild. Stopping is always allowed.
    /// On Linux, Live mode requires kernel 5.9+ and supported filesystems via fanotify.
    /// </summary>
    public bool CanToggleLive => IsLive || IsLiveStarting || (!IsRefreshing && IsLiveSupported);

    /// <summary>
    /// True from the moment a refresh starts until the first repository surfaces (or the
    /// determinate progress bar takes over). Drives an indeterminate "Scanning..." indicator that
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

    /// <summary>
    /// In-memory error log fed by refresh/scan/command catch sites (see <see cref="LogError"/>);
    /// surfaced in the UI by the Errors window/button.
    /// </summary>
    public ErrorLogViewModel ErrorLog { get; } = new();

    /// <summary>Button label reflecting the current error count, e.g. "Errors" or "Errors (3)".</summary>
    public string ErrorsButtonLabel => ErrorLog.Count > 0 ? $"Errors ({ErrorLog.Count})" : "Errors";

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
        OpenInForkCommand = new AsyncRelayCommand<RepositoryNodeViewModel>(OpenInForkAsync);
        CopyToClipboardCommand = new AsyncRelayCommand<RepositoryNodeViewModel>(CopyToClipboardAsync);
        DeepRefreshCommand = new RelayCommand<RepositoryNodeViewModel>(DeepRefreshRepository);
        CheckoutMatchingBranchCommand = new RelayCommand<RepositoryNodeViewModel>(CheckoutMatchingBranch);
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(background: false));
        FetchAndRefreshCommand = new AsyncRelayCommand(() => RefreshAsync(background: false, fetchRemotes: true));
        CleanDownstreamCommand = new AsyncRelayCommand<RepositoryNodeViewModel>(node => CleanDownstreamBranchesAsync(node));
        ToggleLiveCommand = new AsyncRelayCommand(ToggleLiveAsync, () => CanToggleLive);
        ErrorLog.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ErrorLogViewModel.Count))
                OnPropertyChanged(nameof(ErrorsButtonLabel));
        };
        StartUIUpdateThread();
    }

    /// <summary>
    /// Records an error into <see cref="ErrorLog"/>, marshaled onto the UI thread since
    /// <see cref="ObservableCollection{T}"/> is not thread-safe and some catch sites that call this
    /// run on background refresh/command threads.
    /// </summary>
    internal void LogError(string message, string? repositoryPath = null)
        => _ui.Post(() => ErrorLog.AddError(message, repositoryPath));

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
            LogError($"Failed to display alert dialog: {exception.Message}");
        }
    }

    readonly Lock _preparedIndexLock = new();
    MftIndexSession? _preparedDiscoveryIndex;

    internal MftIndexSession? PreparedDiscoveryIndex
    {
        get
        {
            lock (_preparedIndexLock)
                return _preparedDiscoveryIndex;
        }
    }

    internal MftIndexSession? TakePreparedDiscoveryIndex()
    {
        lock (_preparedIndexLock)
        {
            var session = _preparedDiscoveryIndex;
            _preparedDiscoveryIndex = null;
            return session;
        }
    }

    internal async Task SetPreparedDiscoveryIndexAsync(MftIndexSession? nextIndex)
    {
        MftIndexSession? oldIndex;
        lock (_preparedIndexLock)
        {
            oldIndex = _preparedDiscoveryIndex;
            _preparedDiscoveryIndex = nextIndex;
        }

        if (oldIndex != null && !ReferenceEquals(oldIndex, nextIndex))
            await oldIndex.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_liveController != null)
        {
            await _liveController.StopAsync().ConfigureAwait(false);
            _liveController = null;
        }

        await SetPreparedDiscoveryIndexAsync(null).ConfigureAwait(false);
        _searchDebouncer.Dispose();
        GC.SuppressFinalize(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

