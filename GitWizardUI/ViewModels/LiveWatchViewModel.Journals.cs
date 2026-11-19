using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using GitWizard;
using GitWizard.Watch;
using GitWizardUI.Services;
using MFTLib;
using MFTLib.Index;

namespace GitWizardUI.ViewModels;

public sealed partial class LiveWatchViewModel
{
    public ObservableCollection<JournalWarningViewModel> JournalWarnings { get; } = [];
    int _journalGrowInProgress;

    public async Task<JournalResizeResult> EnlargeJournalAsync(
        JournalWarning warning,
        bool allowAboveMaximumRecommendedSize = false)
    {
        if (Interlocked.CompareExchange(ref _journalGrowInProgress, 1, 0) != 0)
        {
            throw new InvalidOperationException("A journal enlargement is already in progress.");
        }

        try
        {
            return await GrowJournalCoreAsync(warning, allowAboveMaximumRecommendedSize).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _journalGrowInProgress, 0);
        }
    }

    internal string? GetGrowRefusalMessage(JournalWarning warning) =>
        TryValidateGrowRequest(warning, out _, out _, out _, out var refusalMessage)
            ? null
            : refusalMessage;

    bool TryValidateGrowRequest(
        JournalWarning warning,
        [NotNullWhen(true)] out LiveWatchController? controller,
        [NotNullWhen(true)] out IUsnJournalGrower? grower,
        out UsnJournalSettings target,
        [NotNullWhen(false)] out string? refusalMessage)
    {
        controller = Controller;
        target = default;
        if (controller?.Source is not IUsnJournalGrower sourceGrower)
        {
            grower = null;
            refusalMessage = "Start Live watching before enlarging a journal.";
            return false;
        }

        grower = sourceGrower;
        if (!IsCurrentWarning(controller, warning))
        {
            refusalMessage = "The drive was re-armed. Review its current journal warning.";
            return false;
        }

        if (!warning.Loss.TryGetGrowthTarget(out target))
        {
            refusalMessage = warning.Loss.GrowthTargetRefusalReason == JournalGrowthTargetRefusalReason.CheckpointNotTrimmed
                ? "A recreated or advanced journal has no size that would have avoided the rescan."
                : warning.GrowUnavailableReason ?? "The journal is already at least as large as the size that would have avoided the rescan.";
            return false;
        }

        if (warning.LastGrowthOutcome == UsnJournalGrowthOutcome.Unchanged)
        {
            refusalMessage = warning.FormatUnchangedReason();
            return false;
        }

        refusalMessage = null;
        return true;
    }

    async Task<JournalResizeResult> GrowJournalCoreAsync(
        JournalWarning warning,
        bool allowAboveMaximumRecommendedSize)
    {
        if (!TryValidateGrowRequest(warning, out var controller, out var grower, out var target, out var refusalMessage))
        {
            throw new InvalidOperationException(refusalMessage);
        }

        var volume = new VolumeKey(warning.DriveLetter[0]);
        var maximumRecommendedSize = grower.QueryMaximumRecommendedSize(volume);
        var effectiveTargetMaximumSize = allowAboveMaximumRecommendedSize
            ? target.MaximumSize
            : Math.Min(target.MaximumSize, maximumRecommendedSize);
        var before = grower.QueryUsnJournalSettings(volume);
        if (before.MaximumSize >= effectiveTargetMaximumSize)
        {
            return new JournalResizeResult(
                before,
                before,
                UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget,
                target.MaximumSize,
                maximumRecommendedSize);
        }

        var growth = await grower.GrowUsnJournalAsync(
            volume,
            target,
            CancellationToken.None,
            allowAboveMaximumRecommendedSize).ConfigureAwait(false);
        var after = growth.JournalSettings;
        await _ui.InvokeAsync(() =>
        {
            if (!ReferenceEquals(controller, Controller))
            {
                return;
            }

            foreach (var row in JournalWarnings.Where(row => !row.IsResolved && HasSameLossIdentity(row.Warning, warning)).ToArray())
            {
                var updatedWarning = row.Warning with
                {
                    MaximumRecommendedSize = growth.MaximumRecommendedSize,
                    CurrentMaximumSize = after.MaximumSize,
                    LastGrowthOutcome = growth.Outcome
                };
                if (IsWarningResolved(growth.Outcome, updatedWarning))
                {
                    row.MarkResolved(updatedWarning);
                }
                else
                {
                    row.ApplyLoss(updatedWarning);
                }
            }
            UpdateJournalWarningHeader();
        }).ConfigureAwait(false);
        return new JournalResizeResult(
            before,
            after,
            growth.Outcome,
            growth.RequestedMaximumSize,
            growth.MaximumRecommendedSize);
    }

    static bool IsCurrentWarning(LiveWatchController controller, JournalWarning warning) =>
        controller.ScanWarnings.Any(current => HasSameLossIdentity(current, warning));

    static bool HasSameLossIdentity(JournalWarning candidate, JournalWarning warning) =>
        string.Equals(candidate.DriveLetter, warning.DriveLetter, StringComparison.OrdinalIgnoreCase) &&
        candidate.VolumeSerial == warning.VolumeSerial &&
        candidate.Loss == warning.Loss;

#pragma warning disable CS8524 // Omit discard arm so CS8509 fails the build when a new UsnJournalGrowthOutcome is added.
    static bool IsWarningResolved(UsnJournalGrowthOutcome outcome, JournalWarning updatedWarning) =>
        outcome switch
        {
            UsnJournalGrowthOutcome.GrewAsRequested => true,
            UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride => true,
            UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended =>
                !updatedWarning.HasGrowAction && !updatedWarning.CanGrowAboveMaximumRecommended,
            UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget =>
                !updatedWarning.HasGrowAction && !updatedWarning.CanGrowAboveMaximumRecommended,
            UsnJournalGrowthOutcome.Unchanged => false
        };
#pragma warning restore CS8524

    void SurfaceNewLiveScanWarnings(LiveWatchController controller)
    {
        var previousRows = JournalWarnings.ToArray();
        var resolvedRows = previousRows.Where(row => row.IsResolved).ToList();
        JournalWarnings.Clear();
        var ignoredSerials = GitWizardConfiguration.GetGlobalConfiguration().IgnoredJournalWarnings;
        var grower = controller.Source as IUsnJournalGrower;
        foreach (var warning in controller.ScanWarnings.OrderBy(warning => warning.DriveLetter, StringComparer.OrdinalIgnoreCase))
        {
            if (ignoredSerials.Contains(warning.VolumeSerial))
            {
                continue;
            }

            var keptRow = previousRows.FirstOrDefault(row => HasSameLossIdentity(row.Warning, warning));
            if (keptRow is not null)
            {
                if (!keptRow.IsResolved)
                {
                    keptRow.ApplyLoss(EnrichWarning(warning, grower, keptRow.Warning));
                }

                resolvedRows.Remove(keptRow);
                JournalWarnings.Add(keptRow);
                continue;
            }

            JournalWarnings.Add(new JournalWarningViewModel(EnrichWarning(warning, grower)));
        }
        foreach (var keptRow in resolvedRows)
        {
            JournalWarnings.Add(keptRow);
        }

        SurfaceCapacityWarnings(controller);
        UpdateJournalWarningHeader();
    }

    static JournalWarning EnrichWarning(JournalWarning warning, IUsnJournalGrower? grower, JournalWarning? previous = null)
    {
        if (grower is null || warning.DriveLetter.Length == 0)
        {
            return warning;
        }

        var volume = new VolumeKey(warning.DriveLetter[0]);
        var maximumRecommendedSize = warning.MaximumRecommendedSize;
        try
        {
            maximumRecommendedSize = grower.QueryMaximumRecommendedSize(volume);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Querying the recommended maximum journal size on {warning.DriveLetter} failed.");
        }

        var currentMaximumSize = warning.CurrentMaximumSize;
        try
        {
            currentMaximumSize = grower.QueryUsnJournalSettings(volume).MaximumSize;
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Querying the current journal settings on {warning.DriveLetter} failed.");
        }

        var lastGrowthOutcome = warning.LastGrowthOutcome ?? previous?.LastGrowthOutcome;
        return warning with
        {
            MaximumRecommendedSize = maximumRecommendedSize,
            CurrentMaximumSize = currentMaximumSize,
            LastGrowthOutcome = lastGrowthOutcome
        };
    }

    internal void RemoveJournalWarning(JournalWarningViewModel warning)
    {
        JournalWarnings.Remove(warning);
        UpdateJournalWarningHeader();
    }

    internal void ClearResolvedJournalWarnings()
    {
        foreach (var row in JournalWarnings.Where(row => row.IsResolved).ToArray())
        {
            JournalWarnings.Remove(row);
        }

        UpdateJournalWarningHeader();
    }

    void UpdateJournalWarningHeader()
    {
        if (!IsLive)
        {
            return;
        }

        var messages = new List<string>();
        var notes = new List<string>();
        if (JournalWarnings.Any(row => !row.IsResolved))
        {
            notes.Add("journal hints");
        }

        if (CapacityWarnings.Count > 0)
        {
            notes.Add("rescan offered");
        }

        if (notes.Count > 0)
        {
            messages.Add($"Live watch active ({string.Join(", ", notes)})");
        }

        if (Controller is { } controller)
        {
            messages.AddRange(controller.ScanErrors.Select(error => $"Live scan error on {error.Key}: {error.Value}"));
        }

        _statusHeader.ShowLiveMessage(messages.Count == 0 ? "Live watch active" : string.Join(" ", messages));
    }
}

public sealed record JournalResizeResult(
    UsnJournalSettings Before,
    UsnJournalSettings After,
    UsnJournalGrowthOutcome Outcome,
    long NeededSize,
    long MaximumRecommendedSize);
