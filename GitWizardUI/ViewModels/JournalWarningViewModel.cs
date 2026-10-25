using System.ComponentModel;
using System.Runtime.CompilerServices;
using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

public sealed class JournalWarningViewModel(JournalWarning warning) : INotifyPropertyChanged
{
    public JournalWarning Warning { get; private set; } = warning;
    public bool HasGrowAction => Warning.HasGrowAction;
    public string? GrowUnavailableReason => Warning.GrowUnavailableReason;

    public void ApplyLoss(JournalWarning warning)
    {
        Warning = warning;
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(HasGrowAction));
        OnPropertyChanged(nameof(GrowUnavailableReason));
    }

    public string DriveLetter => Warning.DriveLetter;

    public string StatusMessage
    {
        get;
        set { field = value; OnPropertyChanged(); }
    } = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
