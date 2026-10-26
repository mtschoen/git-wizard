using System.Collections.ObjectModel;
using System.ComponentModel;
using GitWizard;
using GitWizard.Watch;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.ViewModels;

public sealed class IndexCacheViewModel : INotifyPropertyChanged
{
    readonly IUiDispatcher _dispatcher;
    readonly IUserDialogs _dialogs;
    readonly PreparedDiscoveryIndexStore _preparedDiscoveryIndex;
    readonly Func<MftIndexCacheSnapshot> _inspect;
    readonly Func<MftIndexCacheMaintenanceResult> _clear;
    int _operation;
    public event PropertyChangedEventHandler? PropertyChanged;
    public MftIndexCacheSnapshot? Snapshot { get; private set; }
    public string Text { get; private set; } = "Refresh to inspect the current policy's index cache.";
    public bool IsIdle => Volatile.Read(ref _operation) == 0;
    public ReadOnlyObservableCollection<LiveVolumeStatusViewModel> LiveVolumes { get; }
    public ReadOnlyObservableCollection<VolumeWatchTransition> LiveWatchEvents { get; }

    public IndexCacheViewModel(
        IUiDispatcher dispatcher,
        IUserDialogs dialogs,
        PreparedDiscoveryIndexStore preparedDiscoveryIndex,
        Func<MftIndexCacheSnapshot> inspect,
        Func<MftIndexCacheMaintenanceResult> clear,
        LiveWatchViewModel? liveWatch = null)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _preparedDiscoveryIndex = preparedDiscoveryIndex;
        _inspect = inspect;
        _clear = clear;
        LiveVolumes = liveWatch != null
            ? new ReadOnlyObservableCollection<LiveVolumeStatusViewModel>(liveWatch.LiveVolumes)
            : new ReadOnlyObservableCollection<LiveVolumeStatusViewModel>(
                []);
        LiveWatchEvents = liveWatch != null
            ? new ReadOnlyObservableCollection<VolumeWatchTransition>(liveWatch.LiveWatchEvents)
            : new ReadOnlyObservableCollection<VolumeWatchTransition>(
                []);
    }

    public Task RefreshAsync() => RunExclusiveAsync(async () =>
    {
        var snapshot = await Task.Run(_inspect).ConfigureAwait(false);
        var text = MftIndexCacheStatusFormatter.Format(snapshot);
        await Publish(snapshot, text).ConfigureAwait(false);
    }, "Index cache inspection failed");

    public Task ClearAsync() => RunExclusiveAsync(async () =>
    {
        var result = await Task.Run(_clear).ConfigureAwait(false);
        var outcome = result.Succeeded ? "completed" : "incomplete";
        var text = $"Index cache clear {outcome}.{Environment.NewLine}{result.ToClearText()}";
        await Publish(null, text).ConfigureAwait(false);
    }, "Index cache clear failed");

    public async Task ClearCacheAsync()
    {
        await _preparedDiscoveryIndex.SetAsync(null).ConfigureAwait(false);
        GitWizardApi.ClearCache();
        await _dialogs.ShowAlertAsync("Cache Cleared", "Repository cache has been cleared");
    }

    public async Task DeleteAllLocalFilesAsync()
    {
        await _preparedDiscoveryIndex.SetAsync(null).ConfigureAwait(false);
        if (!GitWizardApi.TryDeleteAllLocalFiles(out var refusal))
        {
            await _dialogs.ShowAlertAsync("Files Not Deleted", refusal);
            return;
        }
        await _dialogs.ShowAlertAsync("Files Deleted", "All local files have been deleted");
    }

    async Task RunExclusiveAsync(Func<Task> operation, string failureHeading)
    {
        if (Interlocked.CompareExchange(ref _operation, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(() => Changed(nameof(IsIdle))).ConfigureAwait(false);
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, failureHeading + ".");
            await Publish(null, $"{failureHeading}: {exception.Message}").ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                Interlocked.Exchange(ref _operation, 0);
                Changed(nameof(IsIdle));
            }).ConfigureAwait(false);
        }
    }

    Task Publish(MftIndexCacheSnapshot? snapshot, string text) => _dispatcher.InvokeAsync(() =>
    {
        Snapshot = snapshot;
        Text = text;
        Changed(nameof(Snapshot));
        Changed(nameof(Text));
    });

    void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
