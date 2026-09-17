using System.ComponentModel;
using System.Runtime.CompilerServices;
using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

public class JournalWarningViewModel : INotifyPropertyChanged
{
    public JournalWarning Warning { get; private set; }
    public bool IsBelowRecommended => Warning.MaximumSize < Warning.RecommendedSize;

    public void ApplySettings(long maximumSize, long allocationDelta)
    {
        Warning = Warning with { MaximumSize = maximumSize, AllocationDelta = allocationDelta };
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(CurrentSizeText));
        OnPropertyChanged(nameof(IsBelowRecommended));
    }

    public string DriveLetter => Warning.DriveLetter;
    public string CurrentSizeText => $"{Warning.MaximumSize / 1024 / 1024} MB";
    public string RecommendedSizeText => $"{Warning.RecommendedSize / 1024 / 1024} MB";

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public JournalWarningViewModel(JournalWarning warning)
    {
        Warning = warning;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
