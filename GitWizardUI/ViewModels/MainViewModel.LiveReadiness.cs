using System.Collections.ObjectModel;
using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    string _liveReadinessText = string.Empty;

    public ObservableCollection<LiveVolumeStatusViewModel> LiveVolumes { get; } = new();

    public string LiveReadinessTooltip =>
        string.Join(Environment.NewLine, LiveVolumes.Select(row => row.DisplayText));

    public string LiveReadinessText
    {
        get => _liveReadinessText;
        internal set
        {
            if (_liveReadinessText == value) return;
            _liveReadinessText = value;
            OnPropertyChanged();
        }
    }

    // UI thread only (callers post through PostIfActive).
    void ApplyLiveVolumeReadiness(VolumeReadinessSnapshot snapshot)
    {
        foreach (var status in snapshot.Volumes)
        {
            var existing = LiveVolumes.FirstOrDefault(row => string.Equals(row.Volume, status.Volume, StringComparison.OrdinalIgnoreCase));
            if (existing is null) LiveVolumes.Add(new LiveVolumeStatusViewModel(status));
            else existing.Update(status);
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
        if (catchingUp.Length > 0) parts.Add($"Catching up: {string.Join(", ", catchingUp)}");
        if (excluded.Length > 0) parts.Add($"excluded: {string.Join(", ", excluded)}");
        LiveReadinessText = string.Join("; ", parts);
    }

    void ApplyLiveReady(WatchReadiness readiness)
    {
        var mode = readiness.Mode switch
        {
            WatchReadinessMode.JournalBacklog => "journal",
            WatchReadinessMode.FilesystemMarks => "marks",
            _ => "pump"
        };
        var text = readiness.CaughtUpVolumes.Count == 0
            ? $"No caught-up volumes ({mode})"
            : $"Ready ({mode}): {string.Join(", ", readiness.CaughtUpVolumes)}";
        if (readiness.ExcludedVolumes.Count > 0)
            text += $"; excluded: {string.Join(", ", readiness.ExcludedVolumes.Select(volume => volume.Volume))}";
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
        LiveReadinessText = string.Empty;
        OnPropertyChanged(nameof(LiveReadinessTooltip));
    }
}
