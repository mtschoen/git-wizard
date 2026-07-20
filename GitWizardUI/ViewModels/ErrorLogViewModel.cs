using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GitWizardUI.ViewModels;

/// <summary>
/// In-memory, bounded log of errors surfaced by refresh/scan/command catch sites that would
/// otherwise only be written to the log file/console (see <c>GitWizardLog</c>). Framework-agnostic
/// so it is directly unit-testable; the view only renders <see cref="Entries"/>.
/// </summary>
public sealed class ErrorLogViewModel : INotifyPropertyChanged
{
    /// <summary>Oldest entries are dropped once the log holds this many.</summary>
    public const int MaxEntries = 300;

    public ObservableCollection<ErrorLogEntry> Entries { get; } = new();

    public int Count => Entries.Count;

    public ErrorLogViewModel()
    {
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Count));
    }

    /// <summary>Records an error. Newest entries appear first; the log is trimmed to <see cref="MaxEntries"/>.</summary>
    public void AddError(string message, string? repositoryPath = null)
    {
        Entries.Insert(0, new ErrorLogEntry(DateTime.Now, repositoryPath, message));
        while (Entries.Count > MaxEntries)
            Entries.RemoveAt(Entries.Count - 1);
    }

    public void Clear() => Entries.Clear();

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
