using System.ComponentModel;
using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

public sealed class LiveVolumeStatusViewModel : INotifyPropertyChanged
{
    VolumeWatchStatus _status;

    public LiveVolumeStatusViewModel(VolumeWatchStatus status) => _status = status;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Volume => _status.Volume;
    public string StateText => _status.State.Token().Humanize();
    public string OriginText => _status.Origin.Token().Humanize();
    public string? Reason => _status.ExclusionReason;
    public string DisplayText => Reason is null
        ? $"{Volume}: {StateText} ({OriginText})"
        : $"{Volume}: {StateText} ({OriginText}): {Reason}";

    public void Update(VolumeWatchStatus status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        foreach (var name in new[] { nameof(StateText), nameof(OriginText), nameof(Reason), nameof(DisplayText) })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
