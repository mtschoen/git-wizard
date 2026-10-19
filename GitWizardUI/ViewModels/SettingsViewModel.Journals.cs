using System.Collections.ObjectModel;
using GitWizard;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.ViewModels;

public partial class SettingsViewModel
{
    readonly MainViewModel? _mainViewModel;
    readonly IUiDispatcher _journalDispatcher;
    int _journalOperationInProgress;

    public ObservableCollection<JournalWarningViewModel> JournalWarnings { get; }

    public async Task EnlargeJournalAsync(JournalWarningViewModel warning)
    {
        if (Interlocked.CompareExchange(ref _journalOperationInProgress, 1, 0) != 0)
            return;
        try
        {
            await _journalDispatcher.InvokeAsync(() => warning.StatusMessage = "Enlarging...").ConfigureAwait(false);
            if (_mainViewModel is null)
                throw new InvalidOperationException("Start Live watching before enlarging a journal.");
            var result = await _mainViewModel.EnlargeJournalAsync(warning.Warning).ConfigureAwait(false);
            await _journalDispatcher.InvokeAsync(() =>
                warning.StatusMessage = $"Before: {result.Before.MaximumSize:N0} bytes. After: {result.After.MaximumSize:N0} bytes.").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Enlarging the journal on {warning.DriveLetter} failed.");
            await _journalDispatcher.InvokeAsync(() => warning.StatusMessage = $"Failed: {exception.Message}").ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _journalOperationInProgress, 0);
        }
    }

    public async Task IgnoreJournalWarningAsync(JournalWarningViewModel warning)
    {
        if (Interlocked.CompareExchange(ref _journalOperationInProgress, 1, 0) != 0)
            return;
        try
        {
            await _configuration.IgnoreJournalWarningAsync(warning.Warning.VolumeSerial).ConfigureAwait(false);
            await _journalDispatcher.InvokeAsync(() =>
            {
                if (_mainViewModel is null)
                    JournalWarnings.Remove(warning);
                else
                    _mainViewModel.RemoveJournalWarning(warning);
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Saving the journal warning preference for {warning.DriveLetter} failed.");
            await _journalDispatcher.InvokeAsync(() => warning.StatusMessage = $"Failed: {exception.Message}").ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _journalOperationInProgress, 0);
        }
    }
}
