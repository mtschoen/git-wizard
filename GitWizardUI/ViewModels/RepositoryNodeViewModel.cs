using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GitWizard;

namespace GitWizardUI.ViewModels;

public class RepositoryNodeViewModel : INotifyPropertyChanged
{
    bool _isExpanded;
    RefreshStatus _status = RefreshStatus.Refreshing;
    string _displayText = string.Empty;

    public GitWizardRepository Repository { get; }
    public ObservableCollection<RepositoryNodeViewModel> Children { get; } = new();

    /// <summary>
    /// True if this node is a group header (drive or remote URL group), not a real repository.
    /// </summary>
    public bool IsGroupHeader { get; init; }

    /// <summary>
    /// The group key label (e.g. remote URL or drive letter), without the expand indicator.
    /// </summary>
    public string GroupKey { get; init; } = string.Empty;

    public bool IsNotGroupHeader => !IsGroupHeader;
    public FontAttributes GroupHeaderFontAttributes => IsGroupHeader ? FontAttributes.Bold : FontAttributes.None;
    public Thickness ItemPadding => IsGroupHeader ? new Thickness(0, 5, 0, 0) : new Thickness(15, 0, 0, 0);

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

    public RefreshStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(StatusTooltip));
                OnPropertyChanged(nameof(IsStatusVisible));
            }
        }
    }

    public string StatusIcon => _status switch
    {
        RefreshStatus.Refreshing => "⟳",
        RefreshStatus.Success => "✓",
        RefreshStatus.Timeout => "⚠",
        RefreshStatus.Error => "✗",
        _ => ""
    };

    public Color StatusColor => _status switch
    {
        RefreshStatus.Refreshing => Colors.Gray,
        RefreshStatus.Success => Colors.Green,
        RefreshStatus.Timeout => Colors.Orange,
        RefreshStatus.Error => Colors.Red,
        _ => Colors.Gray
    };

    public string StatusTooltip => _status switch
    {
        RefreshStatus.Refreshing => "Refreshing...",
        RefreshStatus.Success => $"Refreshed in {Repository.RefreshTimeSeconds}s",
        RefreshStatus.Timeout => Repository.RefreshError ?? "Timed out",
        RefreshStatus.Error => Repository.RefreshError ?? "Unknown error",
        _ => ""
    };

    public bool IsStatusVisible => !IsGroupHeader;

    public string ExpandIndicator => IsExpanded ? "▼" : "▶";

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
    /// Creates a group header node with the given key.
    /// </summary>
    public static RepositoryNodeViewModel CreateGroupHeader(string groupKey)
    {
        var dummy = new GitWizardRepository(string.Empty);
        var node = new RepositoryNodeViewModel(dummy)
        {
            IsGroupHeader = true,
            GroupKey = groupKey,
            _status = RefreshStatus.Success,
        };
        node.UpdateDisplayText();
        return node;
    }

    public void Update()
    {
        if (Repository.IsRefreshing)
            Status = RefreshStatus.Refreshing;
        else if (!string.IsNullOrEmpty(Repository.RefreshError))
            Status = Repository.RefreshError.Contains("Timed out") ? RefreshStatus.Timeout : RefreshStatus.Error;
        else
            Status = RefreshStatus.Success;

        UpdateDisplayText();
    }

    public void UpdateDisplayText()
    {
        if (IsGroupHeader)
        {
            var errorCount = Children.Count(c => c.Status == RefreshStatus.Error);
            var warningCount = Children.Count(c => c.Status == RefreshStatus.Timeout);
            var suffix = "";
            if (errorCount > 0) suffix += $" {errorCount} error{(errorCount > 1 ? "s" : "")}";
            if (warningCount > 0) suffix += $" {warningCount} warning{(warningCount > 1 ? "s" : "")}";
            if (suffix.Length > 0) suffix = " —" + suffix;
            DisplayText = $"{GroupKey} ({Children.Count}){suffix}";
            OnPropertyChanged(nameof(ExpandIndicator));
            return;
        }

        var pendingChanges = Repository.HasPendingChanges;
        var localOnlyCommits = Repository.LocalOnlyCommits;
        var label = WorkingDirectory;

        if (pendingChanges)
        {
            label += $" * ({Repository.NumberOfPendingChanges})";
        }

        if (localOnlyCommits)
        {
            label += " ↑";
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
            FilterType.MyRepositories => IsMyRepository(),
            _ => true
        };
    }

    bool IsMyRepository()
    {
        var email = MainViewModel.GlobalUserEmail;
        if (string.IsNullOrEmpty(email) || Repository.AuthorEmails == null)
            return false;

        return Repository.AuthorEmails.Contains(email);
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

public enum RefreshStatus
{
    Refreshing,
    Success,
    Timeout,
    Error
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
