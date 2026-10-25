using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitWizard;
using GitWizardUI.Services;
using GitWizardUI.ViewModels;

namespace GitWizardUI.Views;

public partial class MainWindow : Window, IAsyncDisposable
{
    readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        Icon = IconLoader.Load();
        var clipboard = new AvaloniaClipboardService(this);
        _viewModel = new MainViewModel(new AvaloniaUiDispatcher(), new AvaloniaUserDialogs(), clipboard);
        DataContext = _viewModel;
        if (!OperatingSystem.IsWindows())
        {
            DefenderButton.IsVisible = false;
        }

        // Scroll a specific repo into view when the view model asks (e.g. after grouping).
        _viewModel.RepositoryList.ScrollToRequest = node => RepositoryList.ScrollIntoView(node);

        // Restore the list's scroll position and selection after a refresh rebuilds the collection.
        _viewModel.RepositoryList.BeforeRepositoriesSwap = CaptureScrollAnchor;
        _viewModel.RepositoryList.AfterRepositoriesSwap = RestoreScrollAndSelectionAnchor;

        // Button.Click drops KeyModifiers in Avalonia (AvaloniaUI/Avalonia#8900), so
        // capture the Shift state from a tunneling PointerPressed before Click is raised.
        RefreshButton.AddHandler(InputElement.PointerPressedEvent, RefreshButton_PointerPressed,
            RoutingStrategies.Tunnel);

        Closed += MainWindow_Closed;
    }

    async void MainWindow_Closed(object? sender, EventArgs e)
    {
        try
        {
            await DisposeAsync();
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception during MainWindow disposal on close.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _viewModel.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    void Window_Loaded(object? sender, RoutedEventArgs e)
        => _ = _viewModel.Refresh.RefreshAsync().RunAndLogFaults("Refreshing repositories");

    public Task ShowSettingsDialogAsync()
        => new SettingsWindow((DataContext as MainViewModel)?.LiveWatch).ShowDialog(this);

    void SettingsMenuItem_Click(object? sender, RoutedEventArgs e)
        => _ = ShowSettingsDialogAsync();

    async void CheckWindowsDefenderMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var success = await Task.Run(() => WindowsDefender.AddExclusions());
        var message = success
            ? WindowsDefender.ExclusionsAddedMessage
            : "Failed to add Windows Defender exclusions. You may need to run as administrator.";
        GitWizardLog.Log(message, success ? GitWizardLog.LogType.Info : GitWizardLog.LogType.Error);
        _viewModel.LogError(message);
        await new AvaloniaUserDialogs().ShowAlertAsync(
            success ? "Defender Exclusions Added" : "Defender Setup Failed", message);
    }

    void ClearCacheMenuItem_Click(object? sender, RoutedEventArgs e)
        => _ = _viewModel.IndexCache.ClearCacheAsync().RunAndLogFaults("Clearing the repository cache");

    void DeleteAllLocalFilesMenuItem_Click(object? sender, RoutedEventArgs e)
        => _ = _viewModel.IndexCache.DeleteAllLocalFilesAsync().RunAndLogFaults("Deleting all local files");

    void IndexCacheStatus_Click(object? sender, RoutedEventArgs arguments)
    {
        var model = _viewModel.IndexCache;
        var window = new IndexCacheStatusWindow(model);
        window.Show(this);
        _ = model.RefreshAsync().RunAndLogFaults("Inspecting the index cache");
    }

    void InspectIndexCache_Click(object? sender, RoutedEventArgs e)
        => _ = _viewModel.IndexCache.RefreshAsync().RunAndLogFaults("Inspecting the index cache");

    void ClearIndexCache_Click(object? sender, RoutedEventArgs e)
        => _ = _viewModel.IndexCache.ClearAsync().RunAndLogFaults("Clearing the index cache");

    void ErrorsMenuItem_Click(object? sender, RoutedEventArgs e)
        => new ErrorLogWindow(_viewModel.ErrorLog).ShowDialog(this);

    // Each sidebar button carries its enum value in Tag (set in XAML via {x:Static}), so a click
    // routes straight to the matching view model action with no per-button dispatch.
    void FilterButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is FilterType filter)
        {
            _viewModel.RepositoryList.ToggleFilter(filter);
        }
    }

    void GroupButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is GroupMode mode)
        {
            _viewModel.RepositoryList.ToggleGroupMode(mode);
        }
    }

    void SortButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SortMode mode)
        {
            _viewModel.RepositoryList.SetSortMode(mode);
        }
    }

    bool _refreshShiftHeld;

    void RefreshButton_PointerPressed(object? sender, PointerPressedEventArgs e)
        => _refreshShiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

    void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        // Consume the Shift state captured at PointerPressed; reset it so a later
        // keyboard activation can't inherit a stale value.
        var hardRefresh = _refreshShiftHeld;
        _refreshShiftHeld = false;

        // Snapshot the scroll anchor now, before RefreshAsync clears the list.
        CaptureScrollAnchor();

        if (hardRefresh)
        {
            _ = _viewModel.Refresh.HardRefreshAsync().RunAndLogFaults("Hard-refreshing repositories");
        }
        else
        {
            _ = _viewModel.Refresh.RefreshAsync().RunAndLogFaults("Refreshing repositories");
        }
    }

    RepositoryNodeViewModel? _lastTappedGroup;
    IPointer? _lastGroupTapPointer;
    ulong _lastGroupTapTimestamp;

    void RepositoryList_Tapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is RepositoryNodeViewModel { IsGroupHeader: true } node)
        {
            var doubleTapTime = ((IInputRoot)this).PlatformSettings?.GetDoubleTapTime(e.Pointer.Type)
                ?? TimeSpan.FromMilliseconds(500);
            var isSecondTap = ReferenceEquals(node, _lastTappedGroup)
                && e.Pointer.Id == _lastGroupTapPointer?.Id
                && e.Timestamp >= _lastGroupTapTimestamp
                && e.Timestamp - _lastGroupTapTimestamp <= (ulong)doubleTapTime.TotalMilliseconds;

            _lastTappedGroup = isSecondTap ? null : node;
            _lastGroupTapPointer = isSecondTap ? null : e.Pointer;
            _lastGroupTapTimestamp = isSecondTap ? 0 : e.Timestamp;
            if (isSecondTap)
            {
                return;
            }

            _viewModel.RepositoryList.ToggleGroupExpand(node);
        }
    }

    void RepositoryList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is RepositoryNodeViewModel { IsGroupHeader: false } node)
        {
            _viewModel.Commands.OpenInExplorer(node);
        }
    }

    // Scroll anchoring across a refresh. RefreshAsync clears the Repositories collection at the
    // start of the refresh (resetting scroll to the top) and rebuilds it with fresh node
    // instances, so the anchor is the repo's stable working-directory path - captured before the
    // refresh, re-applied after the final collection swap.
    string? _anchorPath;
    double _anchorAbove;   // px the anchored row was scrolled above the viewport top
    string? _selectedPath;
    DispatcherTimer? _scrollRestoreTimer;   // drives the closed-loop offset correction after a swap

    // The ListBox's internal ScrollViewer (PART_ScrollViewer); only resolvable once the control
    // template has been applied, so resolve it lazily rather than in the constructor.
    ScrollViewer? ListScrollViewer => RepositoryList.FindDescendantOfType<ScrollViewer>();

    void CaptureScrollAnchor()
    {
        _anchorPath = null;
        _anchorAbove = 0;
        _selectedPath = (RepositoryList.SelectedItem as RepositoryNodeViewModel)?.WorkingDirectory
            ?? _viewModel.RepositoryList.SelectedRepository?.WorkingDirectory;

        var count = RepositoryList.ItemCount;
        for (var i = 0; i < count; i++)
        {
            // The first realized row intersecting the viewport top is the one the user sees at the
            // top; virtualized (off-screen) rows have no container.
            if (RepositoryList.ContainerFromIndex(i) is { } container && container.IsVisible
                && container.TranslatePoint(default, RepositoryList) is { } point
                && point.Y + container.Bounds.Height > 0)
            {
                _anchorPath = (container.DataContext as RepositoryNodeViewModel)?.WorkingDirectory;
                _anchorAbove = Math.Max(0, -point.Y);
                if (GitWizardLog.VerboseMode)
                {
                    GitWizardLog.Log($"[scroll] captured anchor index={i} above={_anchorAbove:F0} path={_anchorPath}");
                }

                break;
            }
        }
    }

    void RestoreScrollAndSelectionAnchor()
    {
        if (_selectedPath is { } selPath)
        {
            var selectedIndex = IndexOfPath(selPath);
            if (selectedIndex >= 0)
            {
                RepositoryList.SelectedIndex = selectedIndex;
            }
        }
        RestoreScrollAnchor();
    }

    void RestoreScrollAnchor()
    {
        if (_anchorPath is not { } path)
        {
            return;
        }

        var above = _anchorAbove;
        _anchorPath = null;

        // Cancel a restore still converging from a previous refresh.
        _scrollRestoreTimer?.Stop();
        _scrollRestoreTimer = null;

        // The rebuilt rows are not laid out at swap time, and the VirtualizingStackPanel's
        // scrollable extent is an *estimate* that is wrong until layout settles
        // (AvaloniaUI/Avalonia#17460/#17848) - so a single Offset set lands against a stale
        // extent and is silently clamped (the old approach: scroll did nothing). Materialize the
        // anchor with ScrollIntoView once, then run a closed loop over successive layout passes:
        // measure where the row actually sits and correct the offset by exactly that delta until
        // it reaches the top (within 1px) or a bounded pass count. Self-heals against the estimate
        // and the settling timing; converges in 1-2 passes (validated headless + real window by
        // the avalonia-vsp-scroll-top spike).
        Dispatcher.UIThread.Post(DispatcherWatchdog.Wrap("Scroll anchor restore", () =>
        {
            var index = IndexOfPath(path);
            if (index < 0)
            {
                if (GitWizardLog.VerboseMode)
                {
                    GitWizardLog.Log($"[scroll] restore skipped: path not found {path}");
                }

                return;
            }
            if (RepositoryList.Items[index] is not { } anchorItem)
            {
                return;
            }

            // Materialize the anchor once. Re-running ScrollIntoView each pass would re-align a
            // partly-off-top row to fully-visible and fight the offset nudge (oscillation).
            RepositoryList.ScrollIntoView(anchorItem);

            var state = new ScrollAnchorState(index, anchorItem, above);
            var convergeTick = DispatcherWatchdog.Wrap("Scroll anchor restore", () => ConvergeScrollAnchor(state));
            _scrollRestoreTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) =>
                    convergeTick());
            _scrollRestoreTimer.Start();
        }), DispatcherPriority.Background);
    }

    int IndexOfPath(string path)
    {
        var count = RepositoryList.ItemCount;
        for (var i = 0; i < count; i++)
        {
            if (RepositoryList.Items[i] is RepositoryNodeViewModel node && node.WorkingDirectory == path)
            {
                return i;
            }
        }

        return -1;
    }

    void FetchAndRefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        CaptureScrollAnchor();
        _ = _viewModel.Refresh.RefreshAsync(fetchRemotes: true)
            .RunAndLogFaults("Fetching remotes and refreshing repositories");
    }

    void LiveButton_Click(object? sender, RoutedEventArgs e)
        => _ = _viewModel.LiveWatch.ToggleLiveAsync().RunAndLogFaults("Toggling Live watch");

    void ForkButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RepositoryNodeViewModel node)
        {
            _ = _viewModel.Commands.OpenInForkAsync(node).RunAndLogFaults("Opening the repository in Fork");
        }
    }

    void DeepRefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RepositoryNodeViewModel node)
        {
            _viewModel.Commands.DeepRefreshRepository(node);
        }
    }

    void CheckoutMatchingBranchButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RepositoryNodeViewModel node)
        {
            _viewModel.Commands.CheckoutMatchingBranch(node);
        }
    }

    void CleanDownstreamButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RepositoryNodeViewModel node)
        {
            _ = _viewModel.Commands.CleanDownstreamBranchesAsync(node).RunAndLogFaults("Cleaning downstream branches");
        }
    }

    void OnOpenInExplorerClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is RepositoryNodeViewModel node)
        {
            _viewModel.Commands.OpenInExplorer(node);
        }
    }

    void OnOpenInForkClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is RepositoryNodeViewModel node)
        {
            _ = _viewModel.Commands.OpenInForkAsync(node).RunAndLogFaults("Opening the repository in Fork");
        }
    }

    void OnCopyToClipboardClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is RepositoryNodeViewModel node)
        {
            _ = _viewModel.Commands.CopyToClipboardAsync(node).RunAndLogFaults("Copying the working directory to the clipboard");
        }
    }

    // ContextMenu.PlacementTarget is always null in Avalonia 11 (AvaloniaUI/Avalonia#16344), so the
    // menu can't reach this row's view model via {Binding $self.PlacementTarget.DataContext}. The
    // owning control's DataContext IS the RepositoryNodeViewModel for the row, so copy it onto the
    // menu when the context menu is requested; the items' Tag/IsVisible bindings then resolve.
    void OnRepositoryContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control control && control.ContextMenu is { } menu)
        {
            menu.DataContext = control.DataContext;
        }
    }
}
