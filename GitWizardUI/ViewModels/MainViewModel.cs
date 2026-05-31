using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using GitWizard;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IUpdateHandler
{
    readonly IUiDispatcher _ui;
    readonly IUserDialogs _dialogs;
    readonly IClipboardService _clipboard;
    readonly ConcurrentDictionary<string, RepositoryNodeViewModel> _repositoryMap = new();
    readonly ConcurrentQueue<RepositoryUICommand> _uiCommands = new();
    readonly Stopwatch _stopwatch = new();
    readonly List<RepositoryNodeViewModel> _allRepositories = new();
    readonly Dictionary<string, RepositoryNodeViewModel> _pendingGroups = new();

    string _headerText = "GitWizard";
    double _progressValue;
    bool _isProgressVisible;
    string _progressText = string.Empty;
    bool _isRefreshing;
    bool _isScanning;
    FilterType _activeFilter = FilterType.None;
    GroupMode _activeGroupMode = GroupMode.None;
    SortMode _activeSortMode = SortMode.WorkingDirectory;
    string? _lastRefreshMessage;
    const int SearchDebounceMilliseconds = 200;
    CancellationTokenSource? _searchDebounceCts;
    string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                OnPropertyChanged();
                DebounceSearch();
            }
        }
    }
    public static string? GlobalUserEmail { get; private set; }

    string? _progressDescription;
    int? _progressTotal;
    int? _progressCount;

    ObservableCollection<RepositoryNodeViewModel> _repositories = new();

    public ObservableCollection<RepositoryNodeViewModel> Repositories
    {
        get => _repositories;
        private set
        {
            if (_repositories != value)
            {
                _repositories = value;
                OnPropertyChanged();
            }
        }
    }

    public string HeaderText
    {
        get => _headerText;
        set
        {
            if (_headerText != value)
            {
                _headerText = value;
                OnPropertyChanged();
            }
        }
    }

    public double ProgressValue
    {
        get => _progressValue;
        set
        {
            if (Math.Abs(_progressValue - value) > 0.001)
            {
                _progressValue = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        set
        {
            if (_isProgressVisible != value)
            {
                _isProgressVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public string ProgressText
    {
        get => _progressText;
        set
        {
            if (_progressText != value)
            {
                _progressText = value;
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
                OnPropertyChanged(nameof(CanRefresh));
            }
        }
    }

    public bool CanRefresh => !IsRefreshing;

    /// <summary>
    /// True from the moment a refresh starts until the first repository surfaces (or the
    /// determinate progress bar takes over). Drives an indeterminate "Scanning…" indicator that
    /// fills the otherwise-empty list during the initial discovery/cache-read gap, which can run
    /// 15-20s on a cold start. Parity with the retired MAUI UI's startup feedback.
    /// </summary>
    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (_isScanning != value)
            {
                _isScanning = value;
                OnPropertyChanged();
            }
        }
    }

    public FilterType ActiveFilter
    {
        get => _activeFilter;
        private set
        {
            if (_activeFilter != value)
            {
                _activeFilter = value;
                OnPropertyChanged();
            }
        }
    }

    public GroupMode ActiveGroupMode
    {
        get => _activeGroupMode;
        private set
        {
            if (_activeGroupMode != value)
            {
                _activeGroupMode = value;
                OnPropertyChanged();
            }
        }
    }

    public SortMode ActiveSortMode
    {
        get => _activeSortMode;
        private set
        {
            if (_activeSortMode != value)
            {
                _activeSortMode = value;
                OnPropertyChanged();
            }
        }
    }

    public Action<RepositoryNodeViewModel>? ScrollToRequest { get; set; }

    /// <summary>
    /// Optional view hook invoked (on the UI thread) immediately after the <see cref="Repositories"/>
    /// collection is swapped, so a desktop shell can restore the user's scroll position after a
    /// refresh rebuilds the list. Avalonia's ListBox resets scroll to the top when its ItemsSource
    /// is replaced (AvaloniaUI/Avalonia#5651) and has no KeepScrollOffset equivalent; MAUI preserves
    /// offset declaratively and leaves this null.
    /// </summary>
    public Action? AfterRepositoriesSwap { get; set; }
    public ICommand OpenInExplorerCommand { get; }
    public ICommand OpenInForkCommand { get; }
    public ICommand CopyToClipboardCommand { get; }
    public ICommand DeepRefreshCommand { get; }
    public ICommand CheckoutMatchingBranchCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand FetchAndRefreshCommand { get; }
    public ICommand CleanDownstreamCommand { get; }

    public MainViewModel(IUiDispatcher ui, IUserDialogs dialogs, IClipboardService clipboard)
    {
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        OpenInExplorerCommand = new RelayCommand<RepositoryNodeViewModel>(OpenInExplorer);
        OpenInForkCommand = new RelayCommand<RepositoryNodeViewModel>(OpenInFork);
        CopyToClipboardCommand = new AsyncRelayCommand<RepositoryNodeViewModel>(CopyToClipboardAsync);
        DeepRefreshCommand = new RelayCommand<RepositoryNodeViewModel>(DeepRefreshRepository);
        CheckoutMatchingBranchCommand = new RelayCommand<RepositoryNodeViewModel>(CheckoutMatchingBranch);
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(background: false));
        FetchAndRefreshCommand = new AsyncRelayCommand(() => RefreshAsync(background: false, fetchRemotes: true));
        CleanDownstreamCommand = new AsyncRelayCommand<RepositoryNodeViewModel>(node => CleanDownstreamBranchesAsync(node));
        StartUIUpdateThread();
    }

    /// <summary>
    /// Marshal a user-facing alert onto the UI thread, logging (not propagating) any dialog
    /// failure so a fire-and-forget alert can't crash the process.
    /// </summary>
    void ShowAlert(string title, string message)
        => _ui.Post(() => _ = DisplayAlertSafeAsync(title, message));

    async Task DisplayAlertSafeAsync(string title, string message)
    {
        try
        {
            await _dialogs.DisplayAlertAsync(title, message);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Failed to display alert dialog.");
        }
    }

    void OpenInExplorer(RepositoryNodeViewModel? node)
    {
        if (node == null)
            return;

        if (node.IsGroupHeader)
        {
            ToggleGroupExpand(node);
            return;
        }

        if (string.IsNullOrEmpty(node.WorkingDirectory))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = node.WorkingDirectory,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            ShowAlert("Error", $"Could not open folder: {ex.Message}");
        }
    }

    void OpenInFork(RepositoryNodeViewModel? node)
    {
        if (node == null || string.IsNullOrEmpty(node.WorkingDirectory))
            return;

        if (!Directory.Exists(node.WorkingDirectory))
        {
            ShowAlert("Error", "Invalid repository path");
            return;
        }

        var configuration = GitWizardConfiguration.GetGlobalConfiguration();
        string? forkPath = configuration.ForkPath;

        if (string.IsNullOrWhiteSpace(forkPath))
        {
            forkPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Fork", "Fork.exe");
        }

        if (!File.Exists(forkPath))
        {
            ShowAlert("Error", $"Fork not found at: {forkPath}\n\nPlease ensure Fork is installed.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = forkPath,
                Arguments = $"\"{node.WorkingDirectory}\"",
                UseShellExecute = false,
                CreateNoWindow = false
            });
        }
        catch (Exception ex)
        {
            ShowAlert("Error", $"Could not launch Fork: {ex.Message}");
        }
    }

    // How long the per-row "✓ Copied" indicator stays lit after a copy before it clears.
    const int CopiedIndicatorMilliseconds = 1500;

    /// <summary>
    /// Copies the node's working directory to the clipboard, then lights the row's transient "copied"
    /// indicator for <see cref="CopiedIndicatorMilliseconds"/> ms before clearing it (this replaced
    /// the old modal "Copied" alert). Wired as the target of an <see cref="AsyncRelayCommand{T}"/>, so
    /// a clipboard failure is logged by that wrapper rather than going unobserved; on failure the
    /// indicator is never lit because the awaited write throws before it is set. Public so the
    /// behavior is awaitable in tests. The indicator flag lives on the VM node, so it is set/reset on
    /// the UI thread via <c>_ui.Post</c>.
    /// </summary>
    public async Task CopyToClipboardAsync(RepositoryNodeViewModel node)
    {
        if (string.IsNullOrEmpty(node.WorkingDirectory))
            return;

        await _clipboard.SetPlainTextAsync(node.WorkingDirectory).ConfigureAwait(false);
        _ui.Post(() => node.JustCopied = true);
        await Task.Delay(CopiedIndicatorMilliseconds).ConfigureAwait(false);
        _ui.Post(() => node.JustCopied = false);
    }

    void DeepRefreshRepository(RepositoryNodeViewModel? node)
    {
        if (node == null || node.IsGroupHeader)
            return;

        node.Status = RefreshStatus.Refreshing;
        Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            node.Repository.Refresh(this, fetchRemotes: true, deepRefresh: true);
            stopwatch.Stop();
            node.Repository.RefreshTimeSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
        });
    }

    void CheckoutMatchingBranch(RepositoryNodeViewModel? node)
    {
        if (node == null || node.IsGroupHeader)
            return;

        if (string.IsNullOrEmpty(node.MatchingBranchName))
            return;

        var branchName = node.MatchingBranchName;
        Task.Run(() =>
        {
            try
            {
                node.Repository.CheckoutBranch(branchName);
                _ui.Post(() => node.Update());
            }
            catch (Exception ex)
            {
                ShowAlert("Checkout Failed", $"Could not check out branch '{node.MatchingBranchName}': {ex.Message}");
            }
        });
    }

    async Task CleanDownstreamBranchesAsync(RepositoryNodeViewModel? node)
    {
        if (node == null || node.IsGroupHeader)
            return;

        var downstream = node.Repository.Branches?.Where(b => b.IsMerged).ToList();
        if (downstream == null || downstream.Count == 0)
            return;

        var branchNames = string.Join(", ", downstream.Select(b => $"'{b.Name}'"));
        var message = $"Delete {downstream.Count} downstream branch(es)?\n\n{branchNames}";

        await _ui.InvokeAsync(async () =>
        {
            await _dialogs.DisplayAlertAsync(
                "Delete Downstream Branches",
                message + "\n\nClick OK to proceed with deletion.",
                "Delete");

            // Run git branch -d for each downstream branch
            var workingDir = node.WorkingDirectory;
            if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir))
            {
                await _dialogs.DisplayAlertAsync("Error", "Invalid repository path");
                return;
            }

            var success = true;
            var failed = new List<string>();

            foreach (var branch in downstream)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"branch -d \"{branch.Name}\"",
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                try
                {
                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        if (!process.WaitForExit(15000))
                        {
                            process.Kill();
                            failed.Add(branch.Name);
                            success = false;
                        }
                        else if (process.ExitCode != 0)
                        {
                            var error = await process.StandardError.ReadToEndAsync();
                            if (!string.IsNullOrEmpty(error) && !error.Contains("not fully merged"))
                            {
                                GitWizardLog.Log($"git branch -d {branch.Name}: {error}", GitWizardLog.LogType.Warning);
                            }
                            if (process.ExitCode != 0)
                            {
                                failed.Add(branch.Name);
                                success = false;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    GitWizardLog.LogException(ex, $"Exception deleting branch {branch.Name} in {workingDir}");
                    failed.Add(branch.Name);
                    success = false;
                }
            }

            if (success)
            {
                var deletedCount = downstream.Count;
                node.Repository.Branches?.RemoveAll(b => b.IsMerged);
                node.UpdateDisplayText();
                await _dialogs.DisplayAlertAsync("Done", $"Deleted {deletedCount} branch(es)");
            }
            else if (failed.Count > 0)
            {
                // Remove successfully deleted branches from Branches so UI doesn't show stale data
                node.Repository.Branches?.RemoveAll(b => b.IsMerged && !failed.Contains(b.Name));
                var failedList = string.Join(", ", failed);
                await _dialogs.DisplayAlertAsync(
                    "Partial Success",
                    $"Could not delete: {failedList}\n\nThese branches may not be fully merged or are protected.");
            }
        });
    }

    void ToggleGroupExpand(RepositoryNodeViewModel? node)
    {
        if (node == null || !node.IsGroupHeader)
            return;

        var index = Repositories.IndexOf(node);
        if (index < 0)
            return;

        if (node.IsExpanded)
        {
            // Collapse: remove children after the header
            node.IsExpanded = false;
            while (index + 1 < Repositories.Count && !Repositories[index + 1].IsGroupHeader)
            {
                Repositories.RemoveAt(index + 1);
            }
        }
        else
        {
            // Expand: insert children after the header
            node.IsExpanded = true;
            var insertIndex = index + 1;
            foreach (var child in node.Children)
            {
                Repositories.Insert(insertIndex++, child);
            }
        }

        node.UpdateDisplayText();
        ScrollToRequest?.Invoke(node);
    }

    void StartUIUpdateThread()
    {
        // Daemon loop for the app's lifetime: drains the UI command queue every 250ms and pushes
        // progress updates onto the UI thread. Never returns by design — it ends with the process.
        // ReSharper disable once FunctionNeverReturns
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(250);

                // Drain all pending commands in one UI dispatch to minimize layout passes
                if (_uiCommands.TryPeek(out _))
                {
                    await _ui.InvokeAsync(() =>
                    {
                        while (_uiCommands.TryDequeue(out var command))
                        {
                            ProcessUICommand(command);
                        }
                    });
                }

                await _ui.InvokeAsync(() =>
                {
                    if (_progressCount.HasValue && _progressTotal.HasValue && _progressTotal.Value > 0)
                    {
                        ProgressValue = (double)_progressCount / _progressTotal.Value;
                        ProgressText = $"{_progressDescription} {_progressCount} / {_progressTotal}";
                        IsProgressVisible = true;

                        if (_progressTotal.Value == _progressCount)
                        {
                            IsProgressVisible = false;
                        }
                    }
                });
            }
        });
    }

    void ProcessUICommand(RepositoryUICommand command)
    {
        switch (command.Type)
        {
            case RepositoryUICommandType.RepositoryCreated:
                if (command.Repository != null)
                    AddRepository(command.Repository);
                break;
            case RepositoryUICommandType.SubmoduleCreated:
                if (command.ParentRepository != null && command.Repository != null)
                    AddSubmodule(command.ParentRepository, command.Repository);
                break;
            case RepositoryUICommandType.WorktreeCreated:
                if (command.Repository != null)
                    AddRepository(command.Repository);
                break;
            case RepositoryUICommandType.UninitializedSubmoduleCreated:
                if (command.ParentRepository != null && command.SubmodulePath != null)
                    AddUninitializedSubmodule(command.ParentRepository, command.SubmodulePath);
                break;
            case RepositoryUICommandType.RefreshCompleted:
                if (command.Repository != null)
                    UpdateCompletedRepository(command.Repository);
                break;
        }
    }

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

    public void UpdateSearchText(string text) => SetSearchText(text);

    // Debounce search-driven filtering. The SearchText setter (bound two-way to the search box)
    // fires per keystroke, and ApplyFilterAndGrouping does a full off-screen rebuild + collection
    // swap — costly with 700+ repos. Coalesce rapid keystrokes into a single filter pass, mirroring
    // the 200ms debounce the retired MAUI UI did in its SearchBox_TextChanged code-behind. The
    // immediate SetSearchText path is left untouched for programmatic/test callers.
    void DebounceSearch()
    {
        var previous = _searchDebounceCts;
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        previous?.Cancel();
        previous?.Dispose();
        _ = RunSearchDebounceAsync(cts.Token);
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

    // The Avalonia sidebar dispatches by button name; route each through the same
    // Toggle*/SetSortMode entry points the MAUI UI used so behavior matches exactly — clicking the
    // active Filter/Group button clears it, Sort always keeps one active — and so the notifying
    // Active* properties update (the sidebar binds its `.active` highlight class to them).
    public void ApplyFilter(string buttonName) => ToggleFilter(buttonName switch
    {
        "FilterPendingChanges" => FilterType.PendingChanges,
        "FilterSubmoduleCheckout" => FilterType.SubmoduleCheckout,
        "FilterSubmoduleUninitialized" => FilterType.SubmoduleUninitialized,
        "FilterSubmoduleConfigIssue" => FilterType.SubmoduleConfigIssue,
        "FilterDetachedHead" => FilterType.DetachedHead,
        "FilterMyRepositories" => FilterType.MyRepositories,
        "FilterLocalOnlyCommits" => FilterType.LocalOnlyCommits,
        "FilterStale" => FilterType.Stale,
        "FilterDownstreamBranches" => FilterType.DownstreamBranches,
        _ => FilterType.None,
    });

    public void ApplyGroup(string buttonName) => ToggleGroupMode(buttonName switch
    {
        "GroupByDrive" => GroupMode.Drive,
        "GroupByRemoteUrl" => GroupMode.RemoteUrl,
        _ => GroupMode.None,
    });

    public void ApplySort(string buttonName) => SetSortMode(buttonName switch
    {
        "SortByWorkingDirectory" => SortMode.WorkingDirectory,
        "SortByRecentlyUsed" => SortMode.RecentlyUsed,
        "SortByRemoteUrl" => SortMode.RemoteUrl,
        "SortBySizeOnDisk" => SortMode.SizeOnDisk,
        _ => SortMode.WorkingDirectory,
    });

    public Task ClearCacheAsync()
    {
        GitWizardApi.ClearCache();
        return _dialogs.DisplayAlertAsync("Cache Cleared", "Repository cache has been cleared");
    }

    public Task DeleteAllLocalFilesAsync()
    {
        GitWizardApi.DeleteAllLocalFiles();
        return _dialogs.DisplayAlertAsync("Files Deleted", "All local files have been deleted");
    }

    void ApplyFilterAndGrouping()
    {
        _pendingGroups.Clear();

        var filtered = _activeFilter == FilterType.None
            ? _allRepositories
            : _allRepositories.Where(r => r.MatchesFilter(_activeFilter, GlobalUserEmail)).ToList();

        if (!string.IsNullOrWhiteSpace(_searchText))
            filtered = filtered.Where(r => r.WorkingDirectory.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        var sorted = ApplySort(filtered);

        // Build off-screen and swap in one shot to avoid per-item layout updates
        var newCollection = new ObservableCollection<RepositoryNodeViewModel>();

        if (_activeGroupMode == GroupMode.None)
        {
            foreach (var repo in sorted)
                newCollection.Add(repo);
        }
        else
        {
            var groups = GroupRepositories(sorted, _activeGroupMode);

            // For remote URL grouping, only show groups with multiple copies (the duplicates you want to clean up)
            var minGroupSize = _activeGroupMode == GroupMode.RemoteUrl ? 2 : 1;

            // Add group headers (collapsed); children are stored on the header node
            foreach (var group in groups.OrderByDescending(g => g.Value.Count))
            {
                if (group.Value.Count < minGroupSize)
                    continue;

                var header = RepositoryNodeViewModel.CreateGroupHeader(group.Key);
                foreach (var repo in ApplySort(group.Value))
                    header.Children.Add(repo);

                // Update display text now that children are added (so count is correct)
                header.UpdateDisplayText();
                newCollection.Add(header);
            }
        }

        // Swap the collection on the UI thread. The view hooks snapshot/restore the ListBox
        // ScrollViewer offset (whose getter enforces UI-thread affinity), and RefreshAsync reaches
        // this method on a thread-pool thread via its ConfigureAwait(false) continuation — so an
        // inline swap there throws "Call from invalid thread". The build above stays off-thread;
        // only the swap is marshaled, preserving the off-screen-build perf intent.
        void SwapInRepositories()
        {
            Repositories = newCollection;
            AfterRepositoriesSwap?.Invoke();
        }

        if (_ui.IsOnUiThread)
            SwapInRepositories();
        else
            _ui.Post(SwapInRepositories);

        UpdateHeaderWithFilterInfo();
    }

    List<RepositoryNodeViewModel> ApplySort(List<RepositoryNodeViewModel> repos)
    {
        return _activeSortMode switch
        {
            SortMode.RecentlyUsed => repos
                .OrderByDescending(r => r.Repository.LastCommitDate ?? DateTimeOffset.MinValue)
                .ToList(),
            SortMode.RemoteUrl => repos
                .OrderBy(r => r.Repository.RemoteUrls.Count > 0
                    ? NormalizeRemoteUrl(r.Repository.RemoteUrls[0])
                    : "\uffff") // sort no-remote to end
                .ToList(),
            SortMode.SizeOnDisk => repos
                .OrderByDescending(r => r.Repository.SizeOnDisk)
                .ToList(),
            _ => repos // WorkingDirectory — already in insertion order (alphabetical from SortedDictionary)
        };
    }

    static Dictionary<string, List<RepositoryNodeViewModel>> GroupRepositories(
        List<RepositoryNodeViewModel> repos, GroupMode mode)
    {
        var groups = new Dictionary<string, List<RepositoryNodeViewModel>>();
        foreach (var repo in repos)
        {
            var keys = GetGroupKeys(repo, mode);
            foreach (var key in keys)
            {
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<RepositoryNodeViewModel>();
                    groups[key] = list;
                }

                list.Add(repo);
            }
        }

        return groups;
    }

    static List<string> GetGroupKeys(RepositoryNodeViewModel repo, GroupMode mode)
    {
        if (mode == GroupMode.Drive)
        {
            var path = repo.WorkingDirectory;
            if (string.IsNullOrEmpty(path))
                return new List<string> { "(unknown)" };

            // Extract drive letter or root path
            var root = Path.GetPathRoot(path);
            return new List<string> { string.IsNullOrEmpty(root) ? "(unknown)" : root };
        }

        if (mode == GroupMode.RemoteUrl)
        {
            var urls = repo.Repository.RemoteUrls;
            if (urls.Count == 0)
                return new List<string> { "(no remote)" };

            // Normalize remote URLs for grouping (strip .git suffix, normalize casing)
            return urls.Select(NormalizeRemoteUrl).Distinct().ToList();
        }

        return new List<string> { "(unknown)" };
    }

    static string NormalizeRemoteUrl(string url)
    {
        url = url.Trim();
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        // Normalize SSH URLs (git@github.com:user/repo) to match HTTPS style for grouping
        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colonIndex = url.IndexOf(':');
            if (colonIndex > 0)
            {
                var host = url[4..colonIndex];
                var path = url[(colonIndex + 1)..];
                url = $"{host}/{path}";
            }
        }
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = url[8..];
        }
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            url = url[7..];
        }

        return url.ToLowerInvariant();
    }

    static string? FindRenamedRepo(GitWizardRepository erroredRepo, string oldPath,
        IReadOnlyDictionary<string, GitWizardRepository> healthyRepos)
    {
        if (erroredRepo.RemoteUrls.Count == 0)
            return null;

        foreach (var remoteUrl in erroredRepo.RemoteUrls)
        {
            var normalizedRemote = NormalizeRemoteUrl(remoteUrl);
            foreach (var (path, healthyRepo) in healthyRepos)
            {
                if (path == oldPath)
                    continue;
                foreach (var healthyRemote in healthyRepo.RemoteUrls)
                {
                    if (NormalizeRemoteUrl(healthyRemote) == normalizedRemote)
                        return path;
                }
            }
        }

        return null;
    }

    void AddToGroups(RepositoryNodeViewModel node)
    {
        var keys = GetGroupKeys(node, _activeGroupMode);
        var minGroupSize = _activeGroupMode == GroupMode.RemoteUrl ? 2 : 1;

        foreach (var key in keys)
        {
            // Find existing group header
            RepositoryNodeViewModel? header = null;
            for (var i = 0; i < Repositories.Count; i++)
            {
                if (Repositories[i].IsGroupHeader && Repositories[i].GroupKey == key)
                {
                    header = Repositories[i];
                    break;
                }
            }

            if (header != null)
            {
                header.Children.Add(node);
                header.UpdateDisplayText();

                // If expanded, insert the node after the last child in the flat list
                if (header.IsExpanded)
                {
                    var headerIndex = Repositories.IndexOf(header);
                    Repositories.Insert(headerIndex + header.Children.Count, node);
                }
            }
            else
            {
                // Create new group header
                header = RepositoryNodeViewModel.CreateGroupHeader(key);
                header.Children.Add(node);
                header.UpdateDisplayText();

                // Only show if meets minimum group size
                if (header.Children.Count >= minGroupSize)
                {
                    Repositories.Add(header);
                }
                else
                {
                    // Keep track of it — it might qualify later when more repos arrive
                    // Store it in the collection anyway for remote URL groups so we can
                    // promote it when a second repo arrives
                    _pendingGroups[key] = header;
                }
            }
        }

        // Check if any pending groups now meet the minimum size
        if (_pendingGroups.Count > 0)
        {
            var promoted = new List<string>();
            foreach (var kvp in _pendingGroups)
            {
                if (kvp.Value.Children.Count >= minGroupSize)
                {
                    kvp.Value.UpdateDisplayText();
                    Repositories.Add(kvp.Value);
                    promoted.Add(kvp.Key);
                }
            }

            foreach (var key in promoted)
                _pendingGroups.Remove(key);
        }

        UpdateHeaderWithFilterInfo();
    }

    void UpdateHeaderWithFilterInfo()
    {
        if (_activeFilter == FilterType.None && _activeGroupMode == GroupMode.None)
        {
            HeaderText = _lastRefreshMessage ?? $"{_allRepositories.Count} repositories";
        }
        else if (_activeGroupMode != GroupMode.None)
        {
            var groupCount = Repositories.Count;
            HeaderText = $"{_allRepositories.Count} repositories in {groupCount} groups";
        }
        else
        {
            HeaderText = $"Showing {Repositories.Count} of {_allRepositories.Count} repositories";
        }
    }

    void AddRepository(GitWizardRepository repository)
    {
        // The first repository to surface ends the "Scanning…" gap; rows now stream into the list.
        IsScanning = false;

        var path = repository.WorkingDirectory;
        if (string.IsNullOrEmpty(path))
            return;

        var node = new RepositoryNodeViewModel(repository);
        _repositoryMap[path] = node;
        _allRepositories.Add(node);

        if (!node.MatchesFilter(_activeFilter, GlobalUserEmail))
            return;

        if (!string.IsNullOrWhiteSpace(_searchText) &&
            !node.WorkingDirectory.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            return;

        if (_activeGroupMode == GroupMode.None)
        {
            Repositories.Add(node);
        }
        else
        {
            AddToGroups(node);
        }
    }

    void AddSubmodule(GitWizardRepository parent, GitWizardRepository submodule)
    {
        var parentPath = parent.WorkingDirectory;
        if (string.IsNullOrEmpty(parentPath) || !_repositoryMap.TryGetValue(parentPath, out var parentNode))
            return;

        var submodulePath = submodule.WorkingDirectory;
        if (string.IsNullOrEmpty(submodulePath))
            return;

        var submoduleNode = new RepositoryNodeViewModel(submodule);
        _repositoryMap[submodulePath] = submoduleNode;
        parentNode.Children.Add(submoduleNode);
    }

    static void AddUninitializedSubmodule(GitWizardRepository parent, string submodulePath)
    {
        // Uninitialized submodules are intentionally not shown in the tree view yet; they could be
        // added as a special node type in the future. Record the skip for verbose diagnostics.
        GitWizardLog.Log($"Skipping uninitialized submodule {submodulePath} under {parent.WorkingDirectory}",
            GitWizardLog.LogType.Verbose);
    }

    void UpdateCompletedRepository(GitWizardRepository repository)
    {
        var path = repository.WorkingDirectory;
        if (string.IsNullOrEmpty(path) || !_repositoryMap.TryGetValue(path, out var node))
            return;

        node.Update();

        // For filtering (no grouping): incrementally add/remove without rebuilding
        if (_activeFilter != FilterType.None && _activeGroupMode == GroupMode.None)
        {
            var isShown = Repositories.Contains(node);
            var shouldShow = node.MatchesFilter(_activeFilter, GlobalUserEmail);
            if (isShown && !shouldShow)
                Repositories.Remove(node);
            else if (!isShown && shouldShow)
                Repositories.Add(node);

            UpdateHeaderWithFilterInfo();
        }
        // When grouping is active, update the parent group header to reflect error/warning counts
        if (_activeGroupMode != GroupMode.None)
        {
            foreach (var item in Repositories)
            {
                if (item.IsGroupHeader && item.Children.Contains(node))
                {
                    item.UpdateDisplayText();
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Hard refresh: clear the cached repository list and report, then refresh.
    /// With no cache present, the refresh runs a full MFT discovery scan, which
    /// self-elevates via UAC on Windows. Bound to Shift+click on the Refresh button.
    /// </summary>
    public async Task HardRefreshAsync()
    {
        if (IsRefreshing)
            return;

        GitWizardApi.ClearCache();
        await RefreshAsync(background: false);
    }

    public async Task RefreshAsync(bool background = false, bool fetchRemotes = false)
    {
        if (IsRefreshing)
            return;

        IsRefreshing = true;
        IsScanning = true;
        HeaderText = fetchRemotes ? "Fetching remotes and refreshing..." : "Refreshing...";

        // Read global git user email for "My Repositories" filter
        if (GlobalUserEmail == null)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "config --global user.email",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process != null)
                {
                    GlobalUserEmail = (await process.StandardOutput.ReadToEndAsync()).Trim();
                }
            }
            catch (Exception exception)
            {
                // Could not read global git user.email; the email filter just will not match.
                GitWizardLog.Log($"Could not read global git user.email: {exception.Message}", GitWizardLog.LogType.Verbose);
            }
        }
        Repositories.Clear();
        _allRepositories.Clear();
        _repositoryMap.Clear();
        _pendingGroups.Clear();

        // Async file I/O for cached repo paths and configuration
        string[]? repositoryPaths = await GitWizardApi.GetCachedRepositoryPathsAsync().ConfigureAwait(false);

        var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync().ConfigureAwait(false);

        HashSet<string> deletedPaths = new();
        HashSet<string> renamedOldPaths = new();

        // Synchronous git scanning (parallel, CPU-bound — runs on thread pool)
        await Task.Run(() =>
        {
            _stopwatch.Restart();
            var report = GitWizardReport.GenerateReport(configuration, repositoryPaths, this, fetchRemotes,
                deepRefresh: fetchRemotes);
            _stopwatch.Stop();

            // Capture deleted paths for cache cleanup on main thread
            deletedPaths = new HashSet<string>(report.DeletedPaths);

            // Detect renamed repos: find errored repos whose remote URLs
            // match a newly discovered (non-error) repo at a different path
            var erroredRepos = report.Repositories
                .Where(kvp => kvp.Value.RefreshError != null)
                .ToList();
            var healthyRepos = report.Repositories
                .Where(kvp => kvp.Value.RefreshError == null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            foreach (var (path, repo) in erroredRepos)
            {
                var newHealthyPath = FindRenamedRepo(repo, path, healthyRepos);
                if (newHealthyPath != null && newHealthyPath != path)
                {
                    renamedOldPaths.Add(path);
                    GitWizardLog.Log($"Repository renamed: {path} -> {newHealthyPath}");
                }
            }

            var refreshMsg = $"Refresh completed in {(float)_stopwatch.ElapsedMilliseconds / 1000:F2} seconds";
            if (deletedPaths.Count > 0)
                refreshMsg += $", {deletedPaths.Count} deleted";
            if (renamedOldPaths.Count > 0)
                refreshMsg += $", {renamedOldPaths.Count} renamed";
            _lastRefreshMessage = refreshMsg;

            if (repositoryPaths == null)
                GitWizardApi.SaveCachedRepositoryPaths(report.GetRepositoryPaths());
        }).ConfigureAwait(false);

        // Update cached repository paths: remove deleted and renamed (old path) entries
        var cachedPaths = GitWizardApi.GetCachedRepositoryPaths();
        if (cachedPaths != null)
        {
            var pathsToRemove = deletedPaths.Union(renamedOldPaths).ToHashSet();
            if (pathsToRemove.Count > 0)
            {
                var updatedPaths = cachedPaths.Where(p => !pathsToRemove.Contains(p)).ToList();
                GitWizardApi.SaveCachedRepositoryPaths(updatedPaths);
            }
        }

        // Remove renamed repos from UI collections (old path entries)
        if (renamedOldPaths.Count > 0)
        {
            var toRemove = _allRepositories.Where(r => renamedOldPaths.Contains(r.WorkingDirectory)).ToList();
            foreach (var node in toRemove)
            {
                _repositoryMap.TryRemove(node.WorkingDirectory, out _);
                _allRepositories.Remove(node);
                Repositories.Remove(node);
            }
        }

        // Wait for the UI command queue to fully drain before applying grouping/sorting
        while (!_uiCommands.IsEmpty)
            await Task.Delay(150).ConfigureAwait(false);

        // One more delay to let the last batch of UI commands finish processing
        await Task.Delay(200).ConfigureAwait(false);

        ApplyFilterAndGrouping();

        if (_activeFilter == FilterType.None && _activeGroupMode == GroupMode.None)
            HeaderText = _lastRefreshMessage ?? $"{_allRepositories.Count} repositories";
        else
            UpdateHeaderWithFilterInfo();

        IsScanning = false;
        IsRefreshing = false;
    }

    // IUpdateHandler implementation

    // Coalesce a burst of status messages into one UI update. A heavy scan (the
    // recursive walk posts per directory) or refresh can fire thousands of these;
    // posting each one floods the dispatcher and starves UI input ("Not Responding").
    // Instead we keep only the latest message and allow at most one post in flight —
    // many updates batch into a single HeaderText write, event-driven (no polling).
    string? _latestStatusMessage;
    int _statusUpdateScheduled;

    public void SendUpdateMessage(string? message)
    {
        if (message == null)
            return;

        _latestStatusMessage = message;

        // Schedule a post only if none is pending; the pending post applies whatever
        // the most recent message is by the time it runs on the UI thread.
        if (Interlocked.CompareExchange(ref _statusUpdateScheduled, 1, 0) == 0)
        {
            _ui.Post(() =>
            {
                Interlocked.Exchange(ref _statusUpdateScheduled, 0);
                HeaderText = _latestStatusMessage;
            });
        }
    }

    public void OnRepositoryCreated(GitWizardRepository gitWizardRepository)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.RepositoryCreated,
            Repository = gitWizardRepository
        });
    }

    public void StartProgress(string description, int total)
    {
        _progressCount = 0;
        _progressDescription = description;
        _progressTotal = total;
        _ui.Post(() =>
        {
            IsScanning = false;
            IsProgressVisible = true;
            ProgressValue = 0;
            ProgressText = $"{description} 0 / {total}";
        });
    }

    public void UpdateProgress(int count)
    {
        _progressCount = count;
    }

    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.SubmoduleCreated,
            ParentRepository = parent,
            Repository = submodule
        });
    }

    public void OnWorktreeCreated(GitWizardRepository worktree)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.WorktreeCreated,
            Repository = worktree
        });
    }

    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.UninitializedSubmoduleCreated,
            ParentRepository = parent,
            SubmodulePath = submodulePath
        });
    }

    public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.RefreshCompleted,
            Repository = gitWizardRepository
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    enum RepositoryUICommandType
    {
        RepositoryCreated,
        SubmoduleCreated,
        WorktreeCreated,
        UninitializedSubmoduleCreated,
        RefreshCompleted
    }

    struct RepositoryUICommand
    {
        public RepositoryUICommandType Type;
        public GitWizardRepository? Repository;
        public GitWizardRepository? ParentRepository;
        public string? SubmodulePath;
    }
}
