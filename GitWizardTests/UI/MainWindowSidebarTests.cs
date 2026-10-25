using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using GitWizardUI.ViewModels;
using GitWizardUI.Views;

namespace GitWizardTests.UI;

/// <summary>
/// Pins the sidebar wiring: every Filter/Group/Sort button carries its typed enum value in
/// <see cref="Avalonia.Controls.Control.Tag"/> (set via {x:Static} in MainWindow.axaml), and the
/// shared Click handlers route it to the matching view model action. Also covers the busy-flag
/// re-entry guards behind the Refresh and Live buttons.
/// </summary>
// Covers MainWindow.axaml, MainWindow.axaml.cs, RefreshCoordinator.cs, RepositoryListViewModel.cs, and StatusHeaderViewModel.cs.
public class MainWindowSidebarTests
{
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaTest]
    public void FilterButtons_CarryTheirFilterTypeInTagAndToggleIt()
    {
        var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        var cases = new (string Name, FilterType Filter)[]
        {
            ("FilterPendingChanges", FilterType.PendingChanges),
            ("FilterSubmoduleCheckout", FilterType.SubmoduleCheckout),
            ("FilterSubmoduleUninitialized", FilterType.SubmoduleUninitialized),
            ("FilterSubmoduleConfigIssue", FilterType.SubmoduleConfigIssue),
            ("FilterDetachedHead", FilterType.DetachedHead),
            ("FilterMyRepositories", FilterType.MyRepositories),
            ("FilterLocalOnlyCommits", FilterType.LocalOnlyCommits),
            ("FilterStale", FilterType.Stale),
            ("FilterDownstreamBranches", FilterType.DownstreamBranches),
            ("FilterBehindRemote", FilterType.BehindRemote),
            ("FilterPublishReady", FilterType.PublishReady),
        };

        foreach (var (name, filter) in cases)
        {
            var button = window.FindControl<Button>(name);
            Assert.That(button, Is.Not.Null, name);
            Assert.That(button!.Tag, Is.EqualTo(filter), $"{name} must carry its FilterType in Tag");

            Click(button);
            Assert.That(viewModel.RepositoryList.ActiveFilter, Is.EqualTo(filter), $"{name} click must activate its filter");
            Assert.That(button.Classes, Does.Contain("active"), $"{name} must show the active highlight");

            Click(button);
            Assert.That(viewModel.RepositoryList.ActiveFilter, Is.EqualTo(FilterType.None), $"{name} re-click must clear the filter");
            Assert.That(button.Classes, Does.Not.Contain("active"), $"{name} must drop the active highlight");
        }
    }

    [AvaloniaTest]
    public void GroupButtons_CarryTheirGroupModeInTagAndToggleIt()
    {
        var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        var cases = new (string Name, GroupMode Mode)[]
        {
            ("GroupByDrive", GroupMode.Drive),
            ("GroupByRemoteUrl", GroupMode.RemoteUrl),
        };

        foreach (var (name, mode) in cases)
        {
            var button = window.FindControl<Button>(name);
            Assert.That(button, Is.Not.Null, name);
            Assert.That(button!.Tag, Is.EqualTo(mode), $"{name} must carry its GroupMode in Tag");

            Click(button);
            Assert.That(viewModel.RepositoryList.ActiveGroupMode, Is.EqualTo(mode), $"{name} click must activate its group mode");

            Click(button);
            Assert.That(viewModel.RepositoryList.ActiveGroupMode, Is.EqualTo(GroupMode.None), $"{name} re-click must clear the grouping");
        }
    }

    [AvaloniaTest]
    public void SortButtons_CarryTheirSortModeInTagAndKeepItOnRepeatClick()
    {
        var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        var cases = new (string Name, SortMode Mode)[]
        {
            ("SortByWorkingDirectory", SortMode.WorkingDirectory),
            ("SortByRecentlyUsed", SortMode.RecentlyUsed),
            ("SortByRemoteUrl", SortMode.RemoteUrl),
            ("SortBySizeOnDisk", SortMode.SizeOnDisk),
        };

        foreach (var (name, mode) in cases)
        {
            var button = window.FindControl<Button>(name);
            Assert.That(button, Is.Not.Null, name);
            Assert.That(button!.Tag, Is.EqualTo(mode), $"{name} must carry its SortMode in Tag");

            Click(button);
            Assert.That(viewModel.RepositoryList.ActiveSortMode, Is.EqualTo(mode), $"{name} click must activate its sort mode");

            Click(button);
            Assert.That(viewModel.RepositoryList.ActiveSortMode, Is.EqualTo(mode), $"{name} re-click must keep the sort mode");
        }
    }

    [AvaloniaTest]
    public async Task RefreshButton_WhileRefreshing_DoesNotReenter()
    {
        await using var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        Dispatcher.UIThread.RunJobs();
        var headerText = window.GetLogicalDescendants().OfType<TextBlock>()
            .Single(block => block.Text == "GitWizard");
        viewModel.Refresh.IsRefreshing = true;
        viewModel.StatusHeader.ShowRefreshMessage("Refreshing...");
        Dispatcher.UIThread.RunJobs();
        Assert.That(headerText.Text, Is.EqualTo("Refreshing..."));

        Click(window.FindControl<Button>("RefreshButton")!);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.Refresh.IsRefreshing, Is.True);
            Assert.That(viewModel.StatusHeader.HeaderText, Is.EqualTo("Refreshing..."),
                "A refresh already in progress must not be re-entered from the button.");
            Assert.That(headerText.Text, Is.EqualTo("Refreshing..."));
        });
        viewModel.StatusHeader.ShowLiveMessage("Live watch active");
        Dispatcher.UIThread.RunJobs();
        Assert.That(headerText.Text, Is.EqualTo("Live watch active"));
    }

    [AvaloniaTest]
    public void LiveButton_WhileRefreshing_IsDisabledAndDoesNotStartLive()
    {
        var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        viewModel.Refresh.IsRefreshing = true;
        var button = window.FindControl<Button>("LiveButton")!;

        Assert.That(button.IsEnabled, Is.False, "CanToggleLive must disable the button mid-refresh.");

        Click(button);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LiveWatch.IsLive, Is.False);
            Assert.That(viewModel.LiveWatch.IsLiveStarting, Is.False);
            Assert.That(viewModel.StatusHeader.HeaderText, Is.EqualTo("GitWizard"),
                "Even a programmatic click must not re-enter ToggleLiveAsync mid-refresh.");
        });
    }
}
