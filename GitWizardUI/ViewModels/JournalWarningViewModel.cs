using System.ComponentModel;
using System.Runtime.CompilerServices;
using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

public class JournalWarningViewModel : INotifyPropertyChanged
{
    public JournalWarning Warning { get; private set; }
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
