using System.Collections.ObjectModel;
using GitWizard;
using GitWizardUI.ViewModels.Services;
using MFTLib;

namespace GitWizardUI.ViewModels;

public partial class SettingsViewModel
{
    readonly LiveWatchViewModel? _liveWatch;
    readonly IUiDispatcher _journalDispatcher;
    int _journalOperationInProgress;

    public ObservableCollection<JournalWarningViewModel> JournalWarnings { get; }

    public async Task EnlargeJournalAsync(
        JournalWarningViewModel warning,
        bool allowAboveMaximumRecommendedSize = false)
    {
        if (Interlocked.CompareExchange(ref _journalOperationInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _journalDispatcher.InvokeAsync(() => warning.StatusMessage = "Enlarging...").ConfigureAwait(false);
            if (_liveWatch is null)
            {
                await _journalDispatcher.InvokeAsync(() =>
                    warning.StatusMessage = "Failed: Start Live watching before enlarging a journal.").ConfigureAwait(false);
                return;
            }

            if (_liveWatch.GetGrowRefusalMessage(warning.Warning) is { } refusalMessage)
            {
                await _journalDispatcher.InvokeAsync(() =>
                    warning.StatusMessage = $"Failed: {refusalMessage}").ConfigureAwait(false);
                return;
            }

            var result = await _liveWatch.EnlargeJournalAsync(
                warning.Warning,
                allowAboveMaximumRecommendedSize).ConfigureAwait(false);
            await _journalDispatcher.InvokeAsync(() =>
                warning.StatusMessage = FormatResizeStatus(result)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Enlarging the journal on {warning.DriveLetter} failed.");
            var failureMessage = _liveWatch?.GetGrowRefusalMessage(warning.Warning) is { } refusalMessage
                ? $"Failed: {refusalMessage}"
                : $"Failed: Could not enlarge the change journal on {warning.DriveLetter}:. Check the log for details.";
            await _journalDispatcher.InvokeAsync(() => warning.StatusMessage = failureMessage).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _journalOperationInProgress, 0);
        }
    }

    static string FormatResizeStatus(JournalResizeResult result)
    {
#pragma warning disable CS8524 // Omit discard arm so CS8509 fails the build when a new UsnJournalGrowthOutcome is added.
        var outcomeSummary = result.Outcome switch
        {
            UsnJournalGrowthOutcome.GrewAsRequested =>
                "Enlarged to the needed size.",
            UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended =>
                "Clamped to the recommended maximum.",
            UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride =>
                "Enlarged above the recommended maximum by override.",
            UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget =>
                "Journal was already at or above the target size.",
            UsnJournalGrowthOutcome.Unchanged =>
                $"Windows accepted the request but kept the journal at {FormatBytes(result.After.MaximumSize)}."
        };
#pragma warning restore CS8524

        return $"Before: {FormatBytes(result.Before.MaximumSize)}. " +
               $"After: {FormatBytes(result.After.MaximumSize)}. " +
               $"Needed: {FormatBytes(result.NeededSize)}. " +
               $"Recommended maximum: {FormatBytes(result.MaximumRecommendedSize)}. " +
               outcomeSummary;
    }

    static string FormatBytes(long bytes) =>
        $"{bytes:N0} bytes ({ByteSizeFormatter.Format(bytes)})";

    public async Task IgnoreJournalWarningAsync(JournalWarningViewModel warning)
    {
        if (Interlocked.CompareExchange(ref _journalOperationInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _configuration.IgnoreJournalWarningAsync(warning.Warning.VolumeSerial).ConfigureAwait(false);
            await _journalDispatcher.InvokeAsync(() =>
            {
                if (_liveWatch is null)
                {
                    JournalWarnings.Remove(warning);
                }
                else
                {
                    _liveWatch.RemoveJournalWarning(warning);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Saving the journal warning preference for {warning.DriveLetter} failed.");
            await _journalDispatcher.InvokeAsync(() =>
                warning.StatusMessage = "Failed: Could not save the journal warning preference. Check the log for details.").ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _journalOperationInProgress, 0);
        }
    }
}
