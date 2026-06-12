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

public partial class MainWindow : Window
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
            DefenderButton.IsVisible = false;

        // Scroll a specific repo into view when the view model asks (e.g. after grouping).
        _viewModel.ScrollToRequest = node => RepositoryList.ScrollIntoView(node);

        // Restore the list's scroll position after a refresh rebuilds the collection. Avalonia
        // resets a ListBox to the top when its ItemsSource is replaced (AvaloniaUI/Avalonia#5651,
        // no KeepScrollOffset); the anchor is captured in the Refresh click handlers, before
        // RefreshAsync clears the collection.
        _viewModel.AfterRepositoriesSwap = RestoreScrollAnchor;

        // Button.Click drops KeyModifiers in Avalonia (AvaloniaUI/Avalonia#8900), so
        // capture the Shift state from a tunneling PointerPressed before Click is raised.
        RefreshButton.AddHandler(InputElement.PointerPressedEvent, RefreshButton_PointerPressed,
            RoutingStrategies.Tunnel);
    }

    void Window_Loaded(object? sender, RoutedEventArgs e)
        => _viewModel.RefreshCommand.Execute(null);

    void SettingsMenuItem_Click(object? sender, RoutedEventArgs e)
        => new SettingsWindow().ShowDialog(this);

    async void CheckWindowsDefenderMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;

        var success = await Task.Run(() => WindowsDefender.AddExclusions());
        await new AvaloniaUserDialogs().DisplayAlertAsync(
            success ? "Defender Exclusions Added" : "Defender Setup Failed",
            success
                ? "Process exclusions for dotnet, git, git-lfs, and git-wizard have been added."
                : "Failed to add Windows Defender exclusions. You may need to run as administrator.");
    }

    void ClearCacheMenuItem_Click(object? sender, RoutedEventArgs e)
        => _ = _viewModel.ClearCacheAsync();

    void DeleteAllLocalFilesMenuItem_Click(object? sender, RoutedEventArgs e)
        => _ = _viewModel.DeleteAllLocalFilesAsync();

    void FilterButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.ApplyFilter((sender as Button)?.Name ?? string.Empty);

    void GroupButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.ApplyGroup((sender as Button)?.Name ?? string.Empty);

    void SortButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.ApplySort((sender as Button)?.Name ?? string.Empty);

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
            _ = _viewModel.HardRefreshAsync();
        else
            _viewModel.RefreshCommand.Execute(null);
    }

    void RepositoryList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // OpenInExplorer decides what to do: toggle a group header, or open a repo.
        if ((e.Source as Control)?.DataContext is RepositoryNodeViewModel node)
            _viewModel.OpenInExplorerCommand.Execute(node);
    }

    // Scroll anchoring across a refresh. RefreshAsync clears the Repositories collection at the
    // start of the refresh (resetting scroll to the top) and rebuilds it with fresh node
    // instances, so the anchor is the repo's stable working-directory path - captured before the
    // refresh, re-applied after the final collection swap.
    string? _anchorPath;
    double _anchorAbove;   // px the anchored row was scrolled above the viewport top
    DispatcherTimer? _scrollRestoreTimer;   // drives the closed-loop offset correction after a swap

    // The ListBox's internal ScrollViewer (PART_ScrollViewer); only resolvable once the control
    // template has been applied, so resolve it lazily rather than in the constructor.
    ScrollViewer? ListScrollViewer => RepositoryList.FindDescendantOfType<ScrollViewer>();

    void CaptureScrollAnchor()
    {
        _anchorPath = null;
        _anchorAbove = 0;

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
                    GitWizardLog.Log($"[scroll] captured anchor index={i} above={_anchorAbove:F0} path={_anchorPath}");
                break;
            }
        }
    }

    void RestoreScrollAnchor()
    {
        if (_anchorPath is not { } path)
            return;
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
        Dispatcher.UIThread.Post(() =>
        {
            var index = IndexOfPath(path);
            if (index < 0)
            {
                if (GitWizardLog.VerboseMode)
                    GitWizardLog.Log($"[scroll] restore skipped: path not found {path}");
                return;
            }
            if (RepositoryList.Items[index] is not { } anchorItem)
                return;

            // Materialize the anchor once. Re-running ScrollIntoView each pass would re-align a
            // partly-off-top row to fully-visible and fight the offset nudge (oscillation).
            RepositoryList.ScrollIntoView(anchorItem);

            var pass = 0;
            _scrollRestoreTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) =>
                {
                    if (++pass > 8)
                    {
                        _scrollRestoreTimer?.Stop();
                        _scrollRestoreTimer = null;
                        return;
                    }
                    if (ListScrollViewer is not { } scrollViewer)
                        return;

                    // If the anchor virtualized away between passes, re-materialize and retry.
                    if (RepositoryList.ContainerFromIndex(index) is not { } container
                        || container.TranslatePoint(default, RepositoryList) is not { } point)
                    {
                        RepositoryList.ScrollIntoView(anchorItem);
                        return;
                    }

                    var delta = point.Y + above;   // converged when point.Y == -above
                    if (Math.Abs(delta) < 1.0)
                    {
                        _scrollRestoreTimer?.Stop();
                        _scrollRestoreTimer = null;
                        if (GitWizardLog.VerboseMode)
                            GitWizardLog.Log($"[scroll] restored index={index} above={above:F0} in {pass} pass(es)");
                        return;
                    }

                    var targetOffsetY = Math.Max(0, scrollViewer.Offset.Y + delta);
                    scrollViewer.Offset = scrollViewer.Offset.WithY(targetOffsetY);
                    if (GitWizardLog.VerboseMode)
                        GitWizardLog.Log($"[scroll] pass {pass} index={index} containerY={point.Y:F0} delta={delta:F0} -> offsetY={targetOffsetY:F0}");
                });
            _scrollRestoreTimer.Start();
        }, DispatcherPriority.Background);
    }

    int IndexOfPath(string path)
    {
        var count = RepositoryList.ItemCount;
        for (var i = 0; i < count; i++)
            if (RepositoryList.Items[i] is RepositoryNodeViewModel node && node.WorkingDirectory == path)
                return i;
        return -1;
    }

    void FetchAndRefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        CaptureScrollAnchor();
        _viewModel.FetchAndRefreshCommand.Execute(null);
    }

    void ForkButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RepositoryNodeViewModel node)
            _viewModel.OpenInForkCommand.Execute(node);
    }

    void DeepRefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RepositoryNodeViewModel node)
            _viewModel.DeepRefreshCommand.Execute(node);
    }

    void CheckoutMatchingBranchButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RepositoryNodeViewModel node)
            _viewModel.CheckoutMatchingBranchCommand.Execute(node);
    }

    void CleanDownstreamButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RepositoryNodeViewModel node)
            _viewModel.CleanDownstreamCommand.Execute(node);
    }

    void OnOpenInExplorerClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is RepositoryNodeViewModel node)
            _viewModel.OpenInExplorerCommand.Execute(node);
    }

    void OnOpenInForkClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is RepositoryNodeViewModel node)
            _viewModel.OpenInForkCommand.Execute(node);
    }

    void OnCopyToClipboardClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is RepositoryNodeViewModel node)
            _viewModel.CopyToClipboardCommand.Execute(node);
    }

    // ContextMenu.PlacementTarget is always null in Avalonia 11 (AvaloniaUI/Avalonia#16344), so the
    // menu can't reach this row's view model via {Binding $self.PlacementTarget.DataContext}. The
    // owning control's DataContext IS the RepositoryNodeViewModel for the row, so copy it onto the
    // menu when the context menu is requested; the items' Tag/IsVisible bindings then resolve.
    void OnRepositoryContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control control && control.ContextMenu is { } menu)
            menu.DataContext = control.DataContext;
    }
}
