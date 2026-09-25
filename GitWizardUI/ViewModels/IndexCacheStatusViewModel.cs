using System.Collections.ObjectModel;
using System.ComponentModel;
using GitWizard;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.ViewModels;

public sealed class IndexCacheStatusViewModel : INotifyPropertyChanged
{
    readonly IUiDispatcher _dispatcher;
    readonly Func<MftIndexCacheSnapshot> _inspect;
    int _refreshing;
    public event PropertyChangedEventHandler? PropertyChanged;
    public MftIndexCacheSnapshot? Snapshot { get; private set; }
    public string Text { get; private set; } = "Refresh to inspect the current policy's index cache.";
    public bool CanRefresh => Volatile.Read(ref _refreshing) == 0;
    public ICommand RefreshCommand { get; }
    public ReadOnlyObservableCollection<LiveVolumeStatusViewModel> LiveVolumes { get; }

    public IndexCacheStatusViewModel(
        IUiDispatcher dispatcher,
        Func<MftIndexCacheSnapshot> inspect,
        ReadOnlyObservableCollection<LiveVolumeStatusViewModel>? liveVolumes = null)
    {
        _dispatcher = dispatcher;
        _inspect = inspect;
        LiveVolumes = liveVolumes ?? new ReadOnlyObservableCollection<LiveVolumeStatusViewModel>(
            new ObservableCollection<LiveVolumeStatusViewModel>());
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanRefresh);
    }

    public async Task RefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            return;
        try
        {
            await _dispatcher.InvokeAsync(() => Changed(nameof(CanRefresh))).ConfigureAwait(false);
            var snapshot = await Task.Run(_inspect).ConfigureAwait(false);
            var text = MftIndexCacheStatusFormatter.Format(snapshot);
            await _dispatcher.InvokeAsync(() =>
            {
                Snapshot = snapshot;
                Text = text;
                Changed(nameof(Snapshot));
                Changed(nameof(Text));
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Index cache status refresh failed.");
            await _dispatcher.InvokeAsync(() =>
            {
                Snapshot = null;
                Text = $"Index cache inspection failed: {exception.Message}";
                Changed(nameof(Snapshot));
                Changed(nameof(Text));
            }).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                Interlocked.Exchange(ref _refreshing, 0);
                Changed(nameof(CanRefresh));
            }).ConfigureAwait(false);
        }
    }

    void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
