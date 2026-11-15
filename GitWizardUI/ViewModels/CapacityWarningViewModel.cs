using System.ComponentModel;
using System.Runtime.CompilerServices;
using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

/// <summary>One drive's capacity warning row, shown under Live and in Settings with its rescan action.</summary>
public sealed class CapacityWarningViewModel(CapacityWarning warning) : INotifyPropertyChanged
{
    public CapacityWarning Warning { get; } = warning;

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
