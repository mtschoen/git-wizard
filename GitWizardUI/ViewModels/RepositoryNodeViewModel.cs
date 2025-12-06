using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GitWizard;

namespace GitWizardUI.ViewModels;

public class RepositoryNodeViewModel : INotifyPropertyChanged
{
    bool _isExpanded;
    bool _isRefreshing = true;
    string _displayText = string.Empty;

    public GitWizardRepository Repository { get; }
    public ObservableCollection<RepositoryNodeViewModel> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set
        {
            if (_isRefreshing != value)
            {
                _isRefreshing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCompleted));
            }
        }
    }

    public bool IsCompleted => !IsRefreshing;

    public string DisplayText
    {
        get => _displayText;
        set
        {
            if (_displayText != value)
            {
                _displayText = value;
                OnPropertyChanged();
            }
        }
    }

    public string WorkingDirectory => Repository.WorkingDirectory ?? "Unknown";

    public RepositoryNodeViewModel(GitWizardRepository repository)
    {
        Repository = repository;
        UpdateDisplayText();
    }

    public void Update()
    {
        IsRefreshing = Repository.IsRefreshing;
        UpdateDisplayText();
    }

    void UpdateDisplayText()
    {
        var pendingChanges = Repository.HasPendingChanges;
        var localOnlyCommits = Repository.LocalOnlyCommits;
        var label = WorkingDirectory;

        if (pendingChanges)
        {
            label += $" * ({Repository.NumberOfPendingChanges})";
        }

        if (localOnlyCommits)
        {
            label += " ↑"; // Up arrow symbol to indicate unpushed/untracked changes
        }

        DisplayText = label;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
