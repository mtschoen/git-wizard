using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GitWizard;

namespace GitWizardUI.ViewModels;

public sealed partial class RepositoryNodeViewModel : INotifyPropertyChanged
{
    RefreshStatus _status = RefreshStatus.Refreshing;
    string? _cachedSizeText;

    public GitWizardRepository Repository { get; }
    public ObservableCollection<RepositoryNodeViewModel> Children { get; } = [];

    /// <summary>
    /// True if this node is a group header (drive or remote URL group), not a real repository.
    /// </summary>
    public bool IsGroupHeader { get; init; }

    /// <summary>
    /// The group key label (e.g. remote URL or drive letter), without the expand indicator.
    /// </summary>
    public string GroupKey { get; private init; } = string.Empty;

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
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
        }
    }

    public RefreshStatus Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusIcon));
            OnPropertyChanged(nameof(StatusColorHex));
            OnPropertyChanged(nameof(StatusTooltip));
            OnPropertyChanged(nameof(IsStatusVisible));
            NotifyParentHeaders();
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
            {
                return "Submodule issue";
            }

            var issues = Repository.SubmoduleHealth.Values.SelectMany(health => health.Issues);
            return "Submodule issues:\n" + string.Join("\n", issues);
        }
    }

    public bool IsStatusVisible => !IsGroupHeader;

    public string ExpandIndicator => IsExpanded ? "▼" : "▶";

    public string DisplayText
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    /// <summary>
    /// Transient flag set true immediately after this row's working directory is copied to the
    /// clipboard, then cleared after a short delay. Drives the row's "✓ Copied" indicator: the view
    /// binds the indicator's visibility to it, while the VM owns the set/reset timing (see
    /// <c>MainViewModel.CopyToClipboardAsync</c>). Kept framework-agnostic per the app convention.
    /// </summary>
    public bool JustCopied
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
        }
    }

    public string WorkingDirectory => Repository.WorkingDirectory ?? "Unknown";

    public string? MatchingBranchName => Repository.MatchingBranchName;

    public bool HasMatchingBranch => !string.IsNullOrEmpty(MatchingBranchName) && Repository.IsDetachedHead;

    /// <summary>
    /// Tooltip for the row label when the current checkout is behind its remote, naming the
    /// last-fetch timestamp so a stale comparison is visible (point 3 of git-wizard#78) - null
    /// (no tooltip) when the repository is not behind. A missing <c>LastFetchTime</c> despite a
    /// nonzero behind count means the remote-tracking ref came from a fetch GitWizard itself never
    /// recorded (e.g. a manual <c>git fetch</c>, or a cache written before this field existed) -
    /// called out explicitly rather than silently treated as fresh.
    /// </summary>
    public string? BehindRemoteTooltip
    {
        get
        {
            var behindCount = Repository.BehindRemoteCount;
            if (behindCount <= 0)
            {
                return null;
            }

            var fetchTime = Repository.LastFetchTime;
            var asOf = fetchTime.HasValue
                ? $"as of last fetch at {fetchTime.Value.LocalDateTime:g}"
                : "as of an untracked fetch (GitWizard has not fetched this repository itself)";
            var commitWord = behindCount == 1 ? "commit" : "commits";
            return $"{behindCount} {commitWord} behind {Repository.CurrentBranch}'s upstream, {asOf}";
        }
    }

    public RepositoryNodeViewModel(GitWizardRepository repository)
    {
        Repository = repository;
        Children.CollectionChanged += OnChildrenCollectionChanged;
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
        {
            Status = RefreshStatus.Refreshing;
        }
        else if (!string.IsNullOrEmpty(Repository.RefreshError))
        {
            Status = Repository.RefreshError.Contains("Timed out") ? RefreshStatus.Timeout : RefreshStatus.Error;
        }
        else if (Repository.HasSubmoduleIssues)
        {
            Status = RefreshStatus.SubmoduleIssue;
        }
        else
        {
            Status = RefreshStatus.Success;
        }

        UpdateDisplayText();
    }

    public void UpdateDisplayText()
    {
        if (IsGroupHeader)
        {
            UpdateGroupDisplayAndSize();
            return;
        }

        _cachedSizeText = ByteSizeFormatter.FormatForDisplay(MeasuredSize);
        OnPropertyChanged(nameof(SizeText));
        NotifyParentHeaders();

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

        var behindRemoteCount = Repository.BehindRemoteCount;
        if (behindRemoteCount > 0)
        {
            label += $" ↓({behindRemoteCount})";
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

        NotifyPruneBadgeChanged();

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
        {
            return;
        }

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
            FilterType.LocalOnlyCommits => Repository.LocalOnlyBranchCount > 0,
            FilterType.Stale => Repository.DaysSinceLastCommit > 30,
            FilterType.DownstreamBranches => HasDownstreamBranches(),
            FilterType.BehindRemote => Repository.BehindRemoteCount > 0,
            FilterType.PublishReady => Repository.IsPublishReady,
            FilterType.Stashes => Repository.StashCount > 0,
            _ => true
        };
    }

    bool IsMyRepository(string? userEmail)
    {
        if (string.IsNullOrEmpty(userEmail) || Repository.AuthorEmails == null)
        {
            return false;
        }

        return Repository.AuthorEmails.Contains(userEmail);
    }

    bool HasPendingChangesRecursive()
    {
        return Repository.HasPendingChanges || Children.Any(c => c.HasPendingChangesRecursive());
    }

    bool HasDetachedHeadRecursive()
    {
        return Repository.IsDetachedHead || Children.Any(c => c.HasDetachedHeadRecursive());
    }

    bool HasSubmoduleCheckoutIssues()
    {
        if (Repository.Submodules == null || Repository.Submodules.Count == 0)
        {
            return false;
        }
        // Submodules with pending changes or local-only commits indicate they're not at the expected pointer
        return Children.Any(c => c.Repository.HasPendingChanges || c.Repository.LocalOnlyCommits || c.HasSubmoduleCheckoutIssues());
    }

    bool HasUninitializedSubmodules()
    {
        return Repository.Submodules?.Values.Any(v => v == null) == true || Children.Any(c => c.HasUninitializedSubmodules());
    }

    bool HasSubmoduleConfigIssues()
    {
        // A submodule config issue is when .gitmodules and the index disagree.
        // We approximate this by checking for submodules that exist in the dictionary but failed to load.
        if (Repository.Submodules == null || Repository.Submodules.Count == 0)
        {
            return false;
        }

        return Repository.Submodules.Values.Any(v => v == null) || Children.Any(c => c.HasSubmoduleConfigIssues());
    }

    bool HasDownstreamBranches() => MergedBranchCount > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum RefreshStatus
{
    Refreshing = 0,
    Success = 1,
    Timeout = 2,
    Error = 3,
    SubmoduleIssue = 4
}

public enum GroupMode
{
    None = 0,
    Drive = 1,
    RemoteUrl = 2
}

public enum SortMode
{
    WorkingDirectory = 0,
    RecentlyUsed = 1,
    RemoteUrl = 2,
    SizeOnDisk = 3,
    Prunable = 4,
    StaleBranches = 5
}

public enum FilterType
{
    None = 0,
    PendingChanges = 1,
    SubmoduleCheckout = 2,
    SubmoduleUninitialized = 3,
    SubmoduleConfigIssue = 4,
    DetachedHead = 5,
    MyRepositories = 6,
    LocalOnlyCommits = 7,
    Stale = 8,
    DownstreamBranches = 9,
    BehindRemote = 10,
    PublishReady = 11,
    Stashes = 12
}
