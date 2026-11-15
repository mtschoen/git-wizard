using System.Collections.ObjectModel;
using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class SettingsViewModel
{
    public ObservableCollection<CapacityWarningViewModel> CapacityWarnings { get; }

    /// <summary>
    /// Rescans the warning's drive by restarting Live (see
    /// <see cref="LiveWatchViewModel.RestartLiveToRescanAsync"/>). The restart replaces the rows, so
    /// a row only keeps a status when the rescan was refused or failed to begin.
    /// </summary>
    public async Task RescanCapacityWarningAsync(CapacityWarningViewModel warning)
    {
        string? refusal;
        try
        {
            refusal = _liveWatch is null
                ? "Open Settings from the main window to rescan."
                : await _liveWatch.RestartLiveToRescanAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Restarting Live watch to rescan {warning.DriveLetter}: failed.");
            refusal = "Could not restart Live watch. Check the log for details.";
        }

        if (refusal is not null)
        {
            await _journalDispatcher.InvokeAsync(() => warning.StatusMessage = $"Failed: {refusal}").ConfigureAwait(false);
        }
    }
}
