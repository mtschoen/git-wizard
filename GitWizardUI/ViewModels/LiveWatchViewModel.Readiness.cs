using System.Collections.ObjectModel;
using System.Globalization;
using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

public sealed partial class LiveWatchViewModel
{
    readonly TimeProvider _timeProvider;
    LiveWatchHistoryViewModel? _liveWatchHistory;

    LiveWatchHistoryViewModel LiveWatchHistory
    {
        get
        {
            if (_liveWatchHistory is not null)
            {
                return _liveWatchHistory;
            }

            _liveWatchHistory = new LiveWatchHistoryViewModel(action => _ui.Post(action), _timeProvider);
            _liveWatchHistory.Changed += () => OnPropertyChanged(nameof(LiveReadinessTooltip));
            return _liveWatchHistory;
        }
    }

    public ObservableCollection<VolumeWatchTransition> LiveWatchEvents => LiveWatchHistory.Events;

    public ObservableCollection<LiveVolumeStatusViewModel> LiveVolumes { get; } = new();

    public string LiveReadinessTooltip
    {
        get
        {
            var current = string.Join(Environment.NewLine, LiveVolumes.Select(row => row.DisplayText));
            var recent = string.Join(Environment.NewLine, LiveWatchEvents.Select(item =>
                $"{item.ObservedAt:HH:mm:ss} {item.Volume}: {item.State}" +
                (item.Reason is null ? string.Empty : $": {item.Reason}")));
            return recent.Length == 0 ? current : current + Environment.NewLine + "Recent watch events" + Environment.NewLine + recent;
        }
    }

    public string LiveReadinessText
    {
        get;
        internal set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    /// <summary>True while a Live start/restart is cold-scanning and a sample has arrived.</summary>
    public bool IsLiveScanProgressVisible
    {
        get;
        internal set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
        }
    }

    public string LiveScanProgressText
    {
        get;
        internal set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    public double LiveScanProgressValue
    {
        get;
        internal set
        {
            if (!(Math.Abs(field - value) > 0.001))
            {
                return;
            }

            field = value;
            OnPropertyChanged();
        }
    }

    public bool IsLiveScanProgressIndeterminate
    {
        get;
        internal set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
        }
    }

    // UI thread only (callers post through PostIfActive).
    void ApplyLiveVolumeReadiness(VolumeReadinessSnapshot snapshot)
    {
        LiveWatchHistory.Apply(snapshot.RecentTransitions);
        foreach (var status in snapshot.Volumes)
        {
            var existing = LiveVolumes.FirstOrDefault(row => string.Equals(row.Volume, status.Volume, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                LiveVolumes.Add(new LiveVolumeStatusViewModel(status));
            }
            else
            {
                existing.Update(status);
            }
        }
        OnPropertyChanged(nameof(LiveReadinessTooltip));
        if (Controller?.Readiness is { } readiness)
        {
            ApplyLiveReady(new WatchReadiness(readiness.Mode, snapshot.CaughtUpVolumes, snapshot.ExcludedVolumes));
            return;
        }
        var catchingUp = snapshot.Volumes
            .Where(volume => volume.State == VolumeWatchState.CatchingUp)
            .Select(volume => volume.Volume).ToArray();
        var excluded = snapshot.ExcludedVolumes.Select(volume => volume.Volume).ToArray();
        var parts = new List<string>();
        if (catchingUp.Length > 0)
        {
            parts.Add($"Catching up: {string.Join(", ", catchingUp)}");
        }

        if (excluded.Length > 0)
        {
            parts.Add($"excluded: {string.Join(", ", excluded)}");
        }

        LiveReadinessText = string.Join("; ", parts);
    }

    void ApplyLiveReady(WatchReadiness readiness)
    {
        var mode = readiness.Mode.Token();
        var text = readiness.CaughtUpVolumes.Count == 0
            ? $"No caught-up volumes ({mode})"
            : $"Ready ({mode}): {string.Join(", ", readiness.CaughtUpVolumes)}";
        if (readiness.ExcludedVolumes.Count > 0)
        {
            text += $"; excluded: {string.Join(", ", readiness.ExcludedVolumes.Select(volume => volume.Volume))}";
        }

        LiveReadinessText = text;
    }

    void ApplyLiveStartupProgress(WatchStartupProgress progress)
    {
        if (progress.Readiness is { } snapshot)
        {
            ApplyLiveVolumeReadiness(snapshot);
            return;
        }
        LiveScanProgressText = progress.Phase switch
        {
            WatchStartupPhase.Opening => "Opening index...",
            WatchStartupPhase.RequestingElevation => "Requesting elevation...",
            WatchStartupPhase.BrokerConnected => "Broker connected",
            WatchStartupPhase.Scanning => $"Scanning {progress.Volume}:",
            WatchStartupPhase.CatchingUp => "Catching up journal...",
            WatchStartupPhase.Discovering => "Discovering repositories...",
            _ => "Reconciling repositories..."
        };
        LiveScanProgressValue = 0;
        IsLiveScanProgressIndeterminate = true;
        IsLiveScanProgressVisible = true;
    }

    void ApplyLiveScanProgress(VolumeScanProgress sample)
    {
        var driveText = $"{sample.Drive}: ({sample.DriveOrdinal} of {sample.DriveTotal})";
        if (sample.TotalRows is > 0)
        {
            LiveScanProgressText = $"Scanning {driveText}, {FormatRecordCount(sample.RowsWritten)} of {FormatRecordCount(sample.TotalRows.Value)} records";
            var driveFraction = Math.Min(1.0, sample.RowsWritten / (double)sample.TotalRows.Value);
            LiveScanProgressValue = (sample.DriveOrdinal - 1 + driveFraction) / Math.Max(1, sample.DriveTotal);
            IsLiveScanProgressIndeterminate = false;
        }
        else
        {
            LiveScanProgressText = $"Scanning {driveText}, {FormatRecordCount(sample.RowsWritten)} records";
            LiveScanProgressValue = 0;
            IsLiveScanProgressIndeterminate = true;
        }

        IsLiveScanProgressVisible = true;
    }

    void ClearLiveScanProgress()
    {
        IsLiveScanProgressVisible = false;
        IsLiveScanProgressIndeterminate = false;
        LiveScanProgressValue = 0;
        LiveScanProgressText = string.Empty;
    }

    void ClearLiveReadiness()
    {
        LiveVolumes.Clear();
        _liveWatchHistory?.Clear();
        LiveReadinessText = string.Empty;
        OnPropertyChanged(nameof(LiveReadinessTooltip));
    }

    static string FormatRecordCount(uint count) => count switch
    {
        < 1_000u => count.ToString(CultureInfo.InvariantCulture),
        < 1_000_000u => string.Create(CultureInfo.InvariantCulture, $"{count / 1_000.0:F1}k"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{count / 1_000_000.0:F1}M")
    };
}
