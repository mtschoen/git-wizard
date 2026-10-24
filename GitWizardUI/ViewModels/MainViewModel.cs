using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using GitWizard;
using GitWizard.Watch;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    readonly IUiDispatcher _ui;
    readonly IUserDialogs _dialogs;
    IndexCacheViewModel? _indexCache;

    public static string? GlobalUserEmail { get; private set; }

    public StatusHeaderViewModel StatusHeader { get; } = new();

    internal PreparedDiscoveryIndexStore PreparedDiscoveryIndex { get; } = new();

    public RepositoryListViewModel RepositoryList { get; }

    public RefreshCoordinator Refresh { get; }

    public LiveWatchViewModel LiveWatch { get; }

    public RepositoryCommandsViewModel Commands { get; }

    public IndexCacheViewModel IndexCache => _indexCache ??= CreateIndexCache();

    /// <summary>
    /// In-memory error log fed by component catch sites via <see cref="ErrorLogViewModel.PostError"/>
    /// and the shell's Defender setup entry (<see cref="LogError"/>); surfaced in the UI by the Errors window/button.
    /// </summary>
    public ErrorLogViewModel ErrorLog { get; } = new();

    /// <summary>Button label reflecting the current error count, e.g. "Errors" or "Errors (3)".</summary>
    public string ErrorsButtonLabel => ErrorLog.Count > 0 ? $"Errors ({ErrorLog.Count})" : "Errors";

    public MainViewModel(IUiDispatcher ui, IUserDialogs dialogs, IClipboardService clipboard,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        _ui = ui;
        _dialogs = dialogs;
        RepositoryList = new RepositoryListViewModel(ui, StatusHeader, ErrorLog);
        Refresh = new RefreshCoordinator(ui, clock, RepositoryList, StatusHeader,
            PreparedDiscoveryIndex, ErrorLog);
        LiveWatch = new LiveWatchViewModel(ui, clock, RepositoryList, Refresh,
            StatusHeader, PreparedDiscoveryIndex, ErrorLog);
        Commands = new RepositoryCommandsViewModel(ui, dialogs, clipboard,
            RepositoryList, Refresh, ErrorLog)
        {
            TimeProvider = clock
        };
        ErrorLog.PropertyChanged += (_, arguments) =>
        {
            if (arguments.PropertyName == nameof(ErrorLogViewModel.Count))
                OnPropertyChanged(nameof(ErrorsButtonLabel));
        };
    }

    IndexCacheViewModel CreateIndexCache()
    {
        var inspection = new MftIndexCacheInspection();
        return new IndexCacheViewModel(_ui, _dialogs, PreparedDiscoveryIndex,
            () => inspection.Capture(GitWizardApi.GetLocalFilesPath(),
                PreparedDiscoveryIndex.CaptureActiveCacheSessions(LiveWatch.Controller?.Source as IndexVolumeChangeSource)),
            () => MftIndexCacheMaintenance.Clear(GitWizardApi.GetLocalFilesPath()),
            LiveWatch);
    }

    // Read the global git user.email once per session for the "My Repositories" filter.
    internal static async Task EnsureGlobalUserEmailAsync()
    {
        if (GlobalUserEmail != null)
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "config --global user.email",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
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
    /// Records a shell-level diagnostic into <see cref="ErrorLog"/> on the UI thread for the
    /// code-behind Defender setup handler; components call <see cref="ErrorLogViewModel.PostError"/> directly.
    /// </summary>
    internal void LogError(string message, string? repositoryPath = null)
        => ErrorLog.PostError(_ui, message, repositoryPath);

    public async ValueTask DisposeAsync()
    {
        Refresh.StopUIDrain();
        await LiveWatch.DisposeAsync().ConfigureAwait(false);
        await PreparedDiscoveryIndex.DisposeAsync().ConfigureAwait(false);
        RepositoryList.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
