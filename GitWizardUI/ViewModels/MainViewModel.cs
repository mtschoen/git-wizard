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
    readonly ConcurrentQueue<RepositoryUICommand> _uiCommands = new();
    readonly object _uiDrainLock = new();
    ITimer? _uiDrainWindow;
    int _uiDrainScheduled;
    volatile bool _uiDrainDisposed;
    readonly CancellationTokenSource _uiDrainShutdown = new();
    HashSet<RepositoryNodeViewModel>? _prePopulatedNodes;
    readonly Stopwatch _stopwatch = new();

    double _progressValue;
    bool _isProgressVisible;
    string _progressText = string.Empty;
    bool _isRefreshing;
    bool _isLive;
    bool _isLiveStarting;
    bool _isScanning;
    public static string? GlobalUserEmail { get; private set; }

    public StatusHeaderViewModel StatusHeader { get; } = new();

    public RepositoryListViewModel RepositoryList { get; }

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
    /// change on disk. Toggled via <see cref="ToggleLiveAsync"/>.
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
    /// Live mode may not be started mid-refresh: a full scan rebuilds
    /// <see cref="RepositoryListViewModel.RepositoryMap"/>/<see cref="RepositoryListViewModel.AllRepositories"/>
    /// from scratch, which would race a live event's incremental add/update/remove against that
    /// rebuild. Stopping is always allowed.
    /// On Linux, Live mode requires kernel 5.9+ and supported filesystems via fanotify.
    /// </summary>
    public bool CanToggleLive => IsLive || IsLiveStarting || (!IsRefreshing && IsLiveSupported);

    /// <summary>
    /// True from the moment a refresh starts until the first repository surfaces (or the
    /// determinate progress bar takes over). Drives an indeterminate "Scanning..." indicator that
    /// fills the otherwise-empty list during the initial discovery/cache-read gap, which can run
    /// 15-20s on a cold start.
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

    /// <summary>
    /// In-memory error log fed by refresh/scan/command catch sites (see <see cref="LogError"/>);
    /// surfaced in the UI by the Errors window/button.
    /// </summary>
    public ErrorLogViewModel ErrorLog { get; } = new();

    /// <summary>Button label reflecting the current error count, e.g. "Errors" or "Errors (3)".</summary>
    public string ErrorsButtonLabel => ErrorLog.Count > 0 ? $"Errors ({ErrorLog.Count})" : "Errors";

    /// <summary>Starts a foreground refresh; a no-op while a refresh is already running.</summary>
    public Task RefreshFromButtonAsync() => RefreshAsync(background: false);

    /// <summary>Fetches every remote, then runs a foreground refresh.</summary>
    public Task FetchAndRefreshAsync() => RefreshAsync(background: false, fetchRemotes: true);

    public MainViewModel(IUiDispatcher ui, IUserDialogs dialogs, IClipboardService clipboard,
        TimeProvider? timeProvider = null)
    {
        _ui = ui;
        RepositoryList = new RepositoryListViewModel(ui, StatusHeader);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _dialogs = dialogs;
        _clipboard = clipboard;
        ErrorLog.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ErrorLogViewModel.Count))
            {
                OnPropertyChanged(nameof(ErrorsButtonLabel));
            }
        };
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
        => _ui.Post(() => _ = ShowAlertSafeAsync(title, message));

    async Task ShowAlertSafeAsync(string title, string message)
    {
        try
        {
            await _dialogs.ShowAlertAsync(title, message);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Failed to display alert dialog.");
            LogError($"Failed to display alert dialog: {exception.Message}");
        }
    }

    internal PreparedDiscoveryIndexStore PreparedDiscoveryIndex { get; } = new();

    public async ValueTask DisposeAsync()
    {
        StopUIDrain();
        if (_liveController != null)
        {
            await _liveController.StopAsync().ConfigureAwait(false);
            _liveController = null;
        }

        await _ui.InvokeAsync(() =>
        {
            ClearLiveReadiness();
            _liveWatchHistory?.Dispose();
            _liveWatchHistory = null;
        }).ConfigureAwait(false);

        await PreparedDiscoveryIndex.DisposeAsync().ConfigureAwait(false);
        RepositoryList.Dispose();
        GC.SuppressFinalize(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
