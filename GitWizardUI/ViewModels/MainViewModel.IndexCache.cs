using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    int _indexCacheOperation;
    string _indexCacheText = "Inspect to show cached blocks in supported GitWizard policy directories.";

    public string IndexCacheText => _indexCacheText;
    public bool CanManageIndexCache => Volatile.Read(ref _indexCacheOperation) == 0;

    public Task InspectIndexCacheAsync() => ManageIndexCacheAsync(clear: false);
    public Task ClearIndexCacheAsync() => ManageIndexCacheAsync(clear: true);

    async Task ManageIndexCacheAsync(bool clear)
    {
        if (Interlocked.CompareExchange(ref _indexCacheOperation, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _ui.InvokeAsync(() => OnPropertyChanged(nameof(CanManageIndexCache))).ConfigureAwait(false);
            var home = GitWizardApi.GetLocalFilesPath();
            var result = await Task.Run(() => clear
                ? MftIndexCacheMaintenance.Clear(home)
                : MftIndexCacheMaintenance.Inspect(home)).ConfigureAwait(false);
            var heading = clear ? "Index cache clear" : "Index cache inspection";
            var outcome = result.Succeeded ? "completed" : "incomplete";
            await _ui.InvokeAsync(() =>
            {
                _indexCacheText = $"{heading} {outcome}.{Environment.NewLine}{result.ToDisplayText()}";
                OnPropertyChanged(nameof(IndexCacheText));
            }).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _indexCacheOperation, 0);
            await _ui.InvokeAsync(() => OnPropertyChanged(nameof(CanManageIndexCache))).ConfigureAwait(false);
        }
    }
}
