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
    public string GroupHeaderFontWeight => IsGroupHeader ? "Bold" : "Normal";
    public string ItemPaddingString => IsGroupHeader ? "0,5,0,0" : "15,0,0,0";
    public string StatusColorHex => _status switch
    {
        RefreshStatus.Refreshing => "#808080",
        RefreshStatus.Success => "#28A745",
        RefreshStatus.Timeout => "#FFA500",
        RefreshStatus.Error => "#DC3545",
        RefreshStatus.SubmoduleIssue => "#8E44AD",
        _ => "#808080",
    };

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
                OnPropertyChanged(nameof(StatusColorHex));
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
        RefreshStatus.SubmoduleIssue => "⚑",
        _ => ""
    };

    public string StatusTooltip => _status switch
    {
        RefreshStatus.Refreshing => "Refreshing...",
        RefreshStatus.Success => $"Refreshed in {Repository.RefreshTimeSeconds}s",
        RefreshStatus.Timeout => Repository.RefreshError ?? "Timed out",
        RefreshStatus.Error => Repository.RefreshError ?? "Unknown error",
        RefreshStatus.SubmoduleIssue => SubmoduleIssueTooltip,
        _ => ""
    };

    /// <summary>
    /// Human-readable summary of every unhealthy submodule on this repository, used as
    /// the tooltip for <see cref="RefreshStatus.SubmoduleIssue"/>.
    /// </summary>
    string SubmoduleIssueTooltip
    {
        get
        {
            if (Repository.SubmoduleHealth is not { Count: > 0 })
                return "Submodule issue";

            var issues = Repository.SubmoduleHealth.Values.SelectMany(health => health.Issues);
            return "Submodule issues:\n" + string.Join("\n", issues);
        }
    }

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

    bool _justCopied;

    /// <summary>
    /// Transient flag set true immediately after this row's working directory is copied to the
    /// clipboard, then cleared after a short delay. Drives the row's "✓ Copied" indicator: the view
    /// binds the indicator's visibility to it, while the VM owns the set/reset timing (see
    /// <c>MainViewModel.CopyToClipboardAsync</c>). Kept framework-agnostic per the app convention.
    /// </summary>
    public bool JustCopied
    {
        get => _justCopied;
        set
        {
            if (_justCopied != value)
            {
                _justCopied = value;
                OnPropertyChanged();
            }
        }
    }

    public string WorkingDirectory => Repository.WorkingDirectory ?? "Unknown";

    public string? MatchingBranchName => Repository.MatchingBranchName;

    public bool HasMatchingBranch => !string.IsNullOrEmpty(MatchingBranchName) && Repository.IsDetachedHead;

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
        else if (Repository.HasSubmoduleIssues)
            Status = RefreshStatus.SubmoduleIssue;
        else
            Status = RefreshStatus.Success;

        UpdateDisplayText();
    }

    public void UpdateDisplayText()
    {
        if (IsGroupHeader)
        {
            var errorCount = Children.Count(c => c.Status == RefreshStatus.Error);
            var warningCount = Children.Count(c => c.Status is RefreshStatus.Timeout or RefreshStatus.SubmoduleIssue);
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

        var daysSinceLastCommit = Repository.DaysSinceLastCommit;
        if (daysSinceLastCommit > 30)
        {
            label += $" ({daysSinceLastCommit}d)";
        }

        if (Repository.IsDetachedHead && HasMatchingBranch)
        {
            label += $" ({MatchingBranchName})";
        }

        var mergedCount = Repository.Branches?.Count(b => b.IsMerged) ?? 0;
        if (mergedCount > 0)
        {
            label += $" [{mergedCount} branch{(mergedCount > 1 ? "es" : "")}]";
        }

        if (Repository.HasSubmoduleIssues)
        {
            var issueCount = Repository.SubmoduleHealth.Count;
            label += issueCount == 1 ? " [submodule issue]" : $" [{issueCount} submodule issues]";
        }

        DisplayText = label;
    }

    public void CheckoutMatchingBranch()
    {
        if (string.IsNullOrEmpty(MatchingBranchName))
            return;

        Repository.CheckoutBranch(MatchingBranchName);
        Update();
    }

    public bool MatchesFilter(FilterType filter, string? userEmail = null)
    {
        return filter switch
        {
            FilterType.None => true,
            FilterType.PendingChanges => HasPendingChangesRecursive(),
            FilterType.SubmoduleCheckout => HasSubmoduleCheckoutIssues(),
            FilterType.SubmoduleUninitialized => HasUninitializedSubmodules(),
            FilterType.SubmoduleConfigIssue => HasSubmoduleConfigIssues(),
            FilterType.DetachedHead => HasDetachedHeadRecursive(),
            FilterType.MyRepositories => IsMyRepository(userEmail),
            FilterType.LocalOnlyCommits => Repository.LocalOnlyCommits,
            FilterType.Stale => Repository.DaysSinceLastCommit > 30,
            FilterType.DownstreamBranches => HasDownstreamBranches(),
            _ => true
        };
    }

    bool IsMyRepository(string? userEmail)
    {
        if (string.IsNullOrEmpty(userEmail) || Repository.AuthorEmails == null)
            return false;

        return Repository.AuthorEmails.Contains(userEmail);
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

    bool HasDownstreamBranches()
    {
        return Repository.Branches?.Any(b => b.IsMerged) == true;
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
    Error,
    SubmoduleIssue
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
    RemoteUrl,
    SizeOnDisk
}

public enum FilterType
{
    None,
    PendingChanges,
    SubmoduleCheckout,
    SubmoduleUninitialized,
    SubmoduleConfigIssue,
    DetachedHead,
    MyRepositories,
    LocalOnlyCommits,
    Stale,
    DownstreamBranches
}
