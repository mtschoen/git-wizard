using System.Collections.ObjectModel;
using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    readonly TimeProvider _timeProvider;
    string _liveReadinessText = string.Empty;
    LiveWatchHistoryViewModel? _liveWatchHistory;

    LiveWatchHistoryViewModel LiveWatchHistory
    {
        get
        {
            if (_liveWatchHistory is not null)
            {
                return _liveWatchHistory;
            }

            _liveWatchHistory = new LiveWatchHistoryViewModel(_ui.Post, _timeProvider);
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
        get => _liveReadinessText;
        internal set
        {
            if (_liveReadinessText == value)
            {
                return;
            }

            _liveReadinessText = value;
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
        if (_liveController?.Readiness is { } readiness)
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

    void ClearLiveReadiness()
    {
        LiveVolumes.Clear();
        _liveWatchHistory?.Clear();
        LiveReadinessText = string.Empty;
        OnPropertyChanged(nameof(LiveReadinessTooltip));
    }
}
