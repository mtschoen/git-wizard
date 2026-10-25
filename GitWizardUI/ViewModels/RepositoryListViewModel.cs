using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.ViewModels;

public sealed partial class RepositoryListViewModel : INotifyPropertyChanged, IDisposable
{
    readonly IUiDispatcher _ui;
    readonly StatusHeaderViewModel _statusHeader;
    readonly ErrorLogViewModel _errorLog;

    internal List<RepositoryNodeViewModel> AllRepositories { get; } = new();
    internal ConcurrentDictionary<string, RepositoryNodeViewModel> RepositoryMap { get; } = new();
    Dictionary<string, RepositoryNodeViewModel> PendingGroups { get; } = new();

    FilterType _activeFilter = FilterType.None;
    GroupMode _activeGroupMode = GroupMode.None;
    SortMode _activeSortMode = SortMode.WorkingDirectory;
    const int SearchDebounceMilliseconds = 200;
    readonly SearchDebouncer _searchDebouncer = new();
    string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
            {
                return;
            }

            _searchText = value;
            OnPropertyChanged();
            DebounceSearch();
        }
    }

    public ObservableCollection<RepositoryNodeViewModel> Repositories
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
        }
    } = new();

    public RepositoryNodeViewModel? SelectedRepository
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

    public FilterType ActiveFilter
    {
        get => _activeFilter;
        private set
        {
            if (_activeFilter == value)
            {
                return;
            }

            _activeFilter = value;
            OnPropertyChanged();
        }
    }

    public GroupMode ActiveGroupMode
    {
        get => _activeGroupMode;
        private set
        {
            if (_activeGroupMode == value)
            {
                return;
            }

            _activeGroupMode = value;
            OnPropertyChanged();
        }
    }

    public SortMode ActiveSortMode
    {
        get => _activeSortMode;
        private set
        {
            if (_activeSortMode == value)
            {
                return;
            }

            _activeSortMode = value;
            OnPropertyChanged();
        }
    }

    public Action<RepositoryNodeViewModel>? ScrollToRequest { get; set; }

    /// <summary>
    /// Optional view hook invoked (on the UI thread) immediately before the <see cref="Repositories"/>
    /// collection is swapped, so a desktop shell can snapshot the user's scroll anchor and selection
    /// before a refresh rebuilds the list.
    /// </summary>
    public Action? BeforeRepositoriesSwap { get; set; }

    /// <summary>
    /// Optional view hook invoked (on the UI thread) immediately after the <see cref="Repositories"/>
    /// collection is swapped, so a desktop shell can restore the user's scroll position after a
    /// refresh rebuilds the list. Avalonia's ListBox resets scroll to the top when its ItemsSource
    /// is replaced (AvaloniaUI/Avalonia#5651) and has no built-in scroll-offset preservation.
    /// </summary>
    public Action? AfterRepositoriesSwap { get; set; }

    internal RepositoryListViewModel(IUiDispatcher ui, StatusHeaderViewModel statusHeader,
        ErrorLogViewModel errorLog)
    {
        _ui = ui;
        _statusHeader = statusHeader;
        _errorLog = errorLog;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose() => _searchDebouncer.Dispose();

    void UpdateHeaderWithFilterInfo() =>
        _statusHeader.ShowFilterInfo(_activeFilter, _activeGroupMode, AllRepositories.Count, Repositories);

    // Sidebar semantics: clicking the active Filter/Group button clears it; Sort always keeps one
    // mode active. The notifying Active* properties drive the sidebar's `.active` highlight class.
    public void ToggleFilter(FilterType filter)
    {
        ActiveFilter = ActiveFilter == filter ? FilterType.None : filter;
        ApplyFilterAndGrouping();
    }

    public void ToggleGroupMode(GroupMode mode)
    {
        ActiveGroupMode = ActiveGroupMode == mode ? GroupMode.None : mode;
        ApplyFilterAndGrouping();
    }

    public void SetSortMode(SortMode mode)
    {
        ActiveSortMode = mode;
        ApplyFilterAndGrouping();
    }

    public void SetSearchText(string text)
    {
        _searchText = text;
        ApplyFilterAndGrouping();
    }

    // Debounce search-driven filtering. The SearchText setter (bound two-way to the search box)
    // fires per keystroke, and ApplyFilterAndGrouping does a full off-screen rebuild + collection
    // swap - costly with 700+ repos. Coalesce rapid keystrokes into one filter pass after a 200ms
    // quiet period. The immediate SetSearchText path is left untouched for programmatic/test callers.
    void DebounceSearch()
    {
        var token = _searchDebouncer.Restart();
        _ = RunSearchDebounceAsync(token);
    }

    async Task RunSearchDebounceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SearchDebounceMilliseconds, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later keystroke; that one owns the pending filter pass.
            return;
        }

        // ApplyFilterAndGrouping builds off-thread and marshals its UI swap internally, so it is
        // safe to invoke from this thread-pool continuation.
        ApplyFilterAndGrouping();
    }

    // suppressAnchorCapture: true only for CompleteRefresh's own final sorted-rebuild call. The
    // anchor for a refresh is captured once, at refresh start, by the window's explicit
    // CaptureScrollAnchor() call before RefreshAsync clears Repositories; that one rebuild's swap
    // must not fire BeforeRepositoriesSwap again and recapture from the rebuilt streaming list,
    // which would overwrite the anchor saved at refresh start. The value is a parameter captured
    // by the posted swap's own closure (not a shared field) so that a second,
    // independently-triggered ApplyFilterAndGrouping call (e.g. a filter/search rebuild whose swap
    // is posted to the UI thread ahead of this one) can never consume this call's suppression or
    // have its own suppressed by it (pr-crew round-9 review of PR #241/#243).
    internal void ApplyFilterAndGrouping(bool suppressAnchorCapture = false)
    {
        PendingGroups.Clear();

        foreach (var repositoryNode in AllRepositories)
        {
            repositoryNode.ClearParentHeaders();
        }

        var filtered = _activeFilter == FilterType.None
            ? AllRepositories
            : AllRepositories.Where(r => r.MatchesFilter(_activeFilter, MainViewModel.GlobalUserEmail)).ToList();

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            filtered = filtered.Where(r => r.WorkingDirectory.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var sorted = RepositorySortOrder.ApplySort(filtered, _activeSortMode);
        var newCollection = BuildRepositoryCollection(sorted);

        if (_ui.IsOnUiThread)
        {
            SwapInRepositories(newCollection, suppressAnchorCapture);
        }
        else
        {
            _ui.Post(() => SwapInRepositories(newCollection, suppressAnchorCapture));
        }
    }

    ObservableCollection<RepositoryNodeViewModel> BuildRepositoryCollection(List<RepositoryNodeViewModel> sorted)
    {
        // Build off-screen and swap in one shot to avoid per-item layout updates
        var newCollection = new ObservableCollection<RepositoryNodeViewModel>();

        if (_activeGroupMode == GroupMode.None)
        {
            foreach (var repo in sorted)
            {
                newCollection.Add(repo);
            }
        }
        else
        {
            var groups = RepositoryGroupKeys.GroupRepositories(sorted, _activeGroupMode);

            // For remote URL grouping, only show groups with multiple copies (the duplicates you want to clean up)
            var minGroupSize = _activeGroupMode == GroupMode.RemoteUrl ? 2 : 1;

            // Add group headers (collapsed); children are stored on the header node
            foreach (var group in groups.OrderByDescending(g => g.Value.Count))
            {
                if (group.Value.Count < minGroupSize)
                {
                    continue;
                }

                var header = RepositoryNodeViewModel.CreateGroupHeader(group.Key);
                foreach (var repo in RepositorySortOrder.ApplySort(group.Value, _activeSortMode))
                {
                    header.Children.Add(repo);
                }

                // Update display text now that children are added (so count is correct)
                header.UpdateDisplayText();
                newCollection.Add(header);
            }
        }

        return newCollection;
    }

    // Swap the collection on the UI thread. The view hooks snapshot/restore the ListBox
    // ScrollViewer offset (whose getter enforces UI-thread affinity), and RefreshAsync reaches
    // ApplyFilterAndGrouping on a thread-pool thread via its ConfigureAwait(false) continuation - so an
    // inline swap there throws "Call from invalid thread", so the swap must run on the UI
    // thread while the build above stays off-thread to preserve the off-screen-build perf win.
    // The header's repository/group count is computed here, after the swap, rather than as a
    // separate call below: computing it on the calling thread right after posting an async
    // swap would read the OLD Repositories collection, showing a stale group count until some
    // later update happened to refresh it (issue #241 addendum: "953 repositories in 2 groups"
    // while six groups were listed).
    void SwapInRepositories(ObservableCollection<RepositoryNodeViewModel> newCollection, bool suppressAnchorCapture)
    {
        var previousSelectedPath = SelectedRepository?.WorkingDirectory;
        if (!suppressAnchorCapture)
        {
            BeforeRepositoriesSwap?.Invoke();
        }

        Repositories = newCollection;
        if (previousSelectedPath != null)
        {
            SelectedRepository = newCollection.FirstOrDefault(n => n.WorkingDirectory == previousSelectedPath)
                ?? AllRepositories.FirstOrDefault(n => n.WorkingDirectory == previousSelectedPath);
        }
        UpdateHeaderWithFilterInfo();
        AfterRepositoriesSwap?.Invoke();
    }
}
