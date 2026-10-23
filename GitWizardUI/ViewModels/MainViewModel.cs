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
    readonly TimeProvider _timeProvider;
    public static string? GlobalUserEmail { get; private set; }

    public StatusHeaderViewModel StatusHeader { get; } = new();

    public RepositoryListViewModel RepositoryList { get; }

    public RefreshCoordinator Refresh { get; }

    public LiveWatchViewModel LiveWatch { get; }

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
        var clock = timeProvider ?? TimeProvider.System;
        _ui = ui;
        _timeProvider = clock;
        RepositoryList = new RepositoryListViewModel(ui, StatusHeader, ErrorLog);
        Refresh = new RefreshCoordinator(ui, clock, RepositoryList, StatusHeader,
            PreparedDiscoveryIndex, ErrorLog);
        LiveWatch = new LiveWatchViewModel(ui, clock, RepositoryList, Refresh,
            StatusHeader, PreparedDiscoveryIndex, ErrorLog);
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
        await LiveWatch.DisposeAsync().ConfigureAwait(false);
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
