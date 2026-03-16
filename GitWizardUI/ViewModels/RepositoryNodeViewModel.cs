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

    /// <summary>
    /// True if this node is a group header (drive or remote URL group), not a real repository.
    /// </summary>
    public bool IsGroupHeader { get; init; }

    public bool IsNotGroupHeader => !IsGroupHeader;
    public FontAttributes GroupHeaderFontAttributes => IsGroupHeader ? FontAttributes.Bold : FontAttributes.None;
    public Thickness ItemPadding => IsGroupHeader ? new Thickness(0, 10, 0, 0) : new Thickness(20, 0, 0, 0);

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

    /// <summary>
    /// Creates a group header node with the given label.
    /// </summary>
    public static RepositoryNodeViewModel CreateGroupHeader(string label)
    {
        // Use a dummy repository for group headers
        var dummy = new GitWizardRepository(string.Empty);
        return new RepositoryNodeViewModel(dummy)
        {
            IsGroupHeader = true,
            _isRefreshing = false,
            _displayText = label
        };
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

    public bool MatchesFilter(FilterType filter)
    {
        return filter switch
        {
            FilterType.None => true,
            FilterType.PendingChanges => HasPendingChangesRecursive(),
            FilterType.SubmoduleCheckout => HasSubmoduleCheckoutIssues(),
            FilterType.SubmoduleUninitialized => HasUninitializedSubmodules(),
            FilterType.SubmoduleConfigIssue => HasSubmoduleConfigIssues(),
            FilterType.DetachedHead => HasDetachedHeadRecursive(),
            FilterType.MyRepositories => false, // TODO: Requires git global user.email lookup
            _ => true
        };
    }

    bool HasPendingChangesRecursive()
    {
        if (Repository.HasPendingChanges) return true;
        return Children.Any(c => c.HasPendingChangesRecursive());
    }

    bool HasDetachedHeadRecursive()
    {
        if (Repository.IsDetachedHead) return true;
        return Children.Any(c => c.HasDetachedHeadRecursive());
    }

    bool HasSubmoduleCheckoutIssues()
    {
        if (Repository.Submodules == null || Repository.Submodules.Count == 0) return false;
        // Submodules with pending changes or local-only commits indicate they're not at the expected pointer
        return Children.Any(c => c.Repository.HasPendingChanges || c.Repository.LocalOnlyCommits || c.HasSubmoduleCheckoutIssues());
    }

    bool HasUninitializedSubmodules()
    {
        if (Repository.Submodules != null && Repository.Submodules.Values.Any(v => v == null)) return true;
        return Children.Any(c => c.HasUninitializedSubmodules());
    }

    bool HasSubmoduleConfigIssues()
    {
        // A submodule config issue is when .gitmodules and the index disagree.
        // We approximate this by checking for submodules that exist in the dictionary but failed to load.
        if (Repository.Submodules == null || Repository.Submodules.Count == 0) return false;
        if (Repository.Submodules.Values.Any(v => v == null)) return true;
        return Children.Any(c => c.HasSubmoduleConfigIssues());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum GroupMode
{
    None,
    Drive,
    RemoteUrl
}

public enum SortMode
{
    WorkingDirectory,
    RecentlyUsed,
    RemoteUrl
}

public enum FilterType
{
    None,
    PendingChanges,
    SubmoduleCheckout,
    SubmoduleUninitialized,
    SubmoduleConfigIssue,
    DetachedHead,
    MyRepositories
}
