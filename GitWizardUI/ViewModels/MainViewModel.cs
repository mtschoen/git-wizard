using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using GitWizard;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    readonly IUiDispatcher _ui;
    readonly IUserDialogs _dialogs;
    readonly IClipboardService _clipboard;
    bool _isLive;
    bool _isLiveStarting;
    public static string? GlobalUserEmail { get; private set; }

    public StatusHeaderViewModel StatusHeader { get; } = new();

    public RepositoryListViewModel RepositoryList { get; }

    public RefreshCoordinator Refresh { get; }

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
    public bool CanToggleLive => IsLive || IsLiveStarting || (!Refresh.IsRefreshing && IsLiveSupported);

    /// <summary>
    /// In-memory error log fed by refresh/scan/command catch sites (see <see cref="LogError"/>);
    /// surfaced in the UI by the Errors window/button.
    /// </summary>
    public ErrorLogViewModel ErrorLog { get; } = new();

    /// <summary>Button label reflecting the current error count, e.g. "Errors" or "Errors (3)".</summary>
    public string ErrorsButtonLabel => ErrorLog.Count > 0 ? $"Errors ({ErrorLog.Count})" : "Errors";

    public MainViewModel(IUiDispatcher ui, IUserDialogs dialogs, IClipboardService clipboard,
        TimeProvider? timeProvider = null)
    {
        _ui = ui;
        _timeProvider = timeProvider ?? TimeProvider.System;
        RepositoryList = new RepositoryListViewModel(ui, StatusHeader, ErrorLog);
        Refresh = new RefreshCoordinator(ui, _timeProvider, RepositoryList, StatusHeader,
            PreparedDiscoveryIndex, ErrorLog);
        Refresh.PropertyChanged += (_, arguments) =>
        {
            if (arguments.PropertyName == nameof(RefreshCoordinator.IsRefreshing))
            {
                OnPropertyChanged(nameof(CanToggleLive));
                OnPropertyChanged(nameof(LiveTooltip));
            }
        };
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

    // Read the global git user.email once per session for the "My Repositories" filter.
    internal static async Task EnsureGlobalUserEmailAsync()
    {
        if (GlobalUserEmail != null)
            return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "config --global user.email",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
                GlobalUserEmail = (await process.StandardOutput.ReadToEndAsync()).Trim();
        }
        catch (Exception exception)
        {
            // Could not read global git user.email; the email filter just will not match.
            GitWizardLog.Log($"Could not read global git user.email: {exception.Message}", GitWizardLog.LogType.Verbose);
        }
    }

    /// <summary>
    /// Records an error into <see cref="ErrorLog"/>, marshaled onto the UI thread since
    /// <see cref="ObservableCollection{T}"/> is not thread-safe and some catch sites that call this
    /// run on background refresh/command threads.
    /// </summary>
    internal void LogError(string message, string? repositoryPath = null)
        => ErrorLog.PostError(_ui, message, repositoryPath);

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
        Refresh.StopUIDrain();
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
