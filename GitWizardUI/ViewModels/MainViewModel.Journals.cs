using System.Collections.ObjectModel;
using GitWizard;
using GitWizard.Watch;
using GitWizardUI.Services;
using MFTLib;
using MFTLib.Index;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<JournalWarningViewModel> JournalWarnings { get; } = new();
    int _journalGrowInProgress;

    public async Task<JournalResizeResult> EnlargeJournalAsync(JournalWarning warning)
    {
        if (Interlocked.CompareExchange(ref _journalGrowInProgress, 1, 0) != 0)
            throw new InvalidOperationException("A journal enlargement is already in progress.");
        try
        {
            return await GrowJournalCoreAsync(warning).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _journalGrowInProgress, 0);
        }
    }

    async Task<JournalResizeResult> GrowJournalCoreAsync(JournalWarning warning)
    {
        var controller = _liveController;
        if (controller?.Source is not IUsnJournalGrower grower)
            throw new InvalidOperationException("Start Live watching before enlarging a journal.");
        if (!controller.ScanWarnings.Contains(warning))
            throw new InvalidOperationException("The drive was re-armed. Review its current journal warning.");

        if (!warning.Loss.TryGetGrowthTarget(out var target))
        {
            if (warning.Loss.Cause != JournalCheckpointLossCause.CheckpointTrimmed)
                throw new InvalidOperationException("A recreated or advanced journal has no size that would have avoided the rescan.");
            throw new InvalidOperationException(warning.GrowUnavailableReason);
        }

        var before = grower.QueryUsnJournalSettings(warning.DriveLetter[0]);
        if (before.MaximumSize >= target.MaximumSize)
            throw new InvalidOperationException("The journal already meets the suggested size. Refusing to shrink or resize.");

        var after = await grower.GrowUsnJournalAsync(warning.DriveLetter[0], target.MaximumSize,
            target.AllocationDelta, CancellationToken.None).ConfigureAwait(false);
        await _ui.InvokeAsync(() =>
        {
            if (!ReferenceEquals(controller, _liveController))
                return;
            var resolved = new List<JournalWarningViewModel>();
            foreach (var row in JournalWarnings.Where(row => row.Warning == warning))
            {
                var updatedLoss = warning.Loss with { JournalSettings = after };
                var updatedWarning = row.Warning with { Loss = updatedLoss };
                if (!updatedWarning.HasGrowAction)
                    resolved.Add(row);
                else
                    row.ApplyLoss(updatedWarning);
            }
            foreach (var row in resolved)
                JournalWarnings.Remove(row);
            UpdateJournalWarningHeader();
        }).ConfigureAwait(false);
        return new JournalResizeResult(before, after);
    }

    void SurfaceNewLiveScanWarnings(LiveWatchController controller)
    {
        JournalWarnings.Clear();
        var ignoredSerials = GitWizardConfiguration.GetGlobalConfiguration().IgnoredJournalWarnings;
        foreach (var warning in controller.ScanWarnings.OrderBy(warning => warning.DriveLetter, StringComparer.OrdinalIgnoreCase))
        {
            if (!ignoredSerials.Contains(warning.VolumeSerial))
                JournalWarnings.Add(new JournalWarningViewModel(warning));
        }
        UpdateJournalWarningHeader();
    }

    internal void RemoveJournalWarning(JournalWarningViewModel warning)
    {
        JournalWarnings.Remove(warning);
        UpdateJournalWarningHeader();
    }

    void UpdateJournalWarningHeader()
    {
        if (!IsLive)
            return;
        var messages = new List<string>();
        if (JournalWarnings.Any())
            messages.Add("Live watch active (journal hints)");
        if (_liveController is { } controller)
            messages.AddRange(controller.ScanErrors.Select(error => $"Live scan error on {error.Key}: {error.Value}"));
        HeaderText = messages.Count == 0 ? "Live watch active" : string.Join(" ", messages);
    }
}

public sealed record JournalResizeResult(UsnJournalSettings Before, UsnJournalSettings After);
