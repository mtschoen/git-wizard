using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using GitWizard;
using GitWizardUI.ViewModels;
using GitWizardUI.Views;

namespace GitWizardTests.UI;

// Covers MainWindow.axaml and RepositoryListViewModel.cs: component binding updates.
public class MainWindowRepositoryListBindingTests
{
    [AvaloniaTest]
    public async Task ListBoxItemsSource_FollowsRepositoryListSwap()
    {
        await using var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        var list = window.FindControl<ListBox>("RepositoryList")!;
        Dispatcher.UIThread.RunJobs();
        var previous = viewModel.RepositoryList.Repositories;
        Assert.That(list.ItemsSource, Is.SameAs(previous));

        viewModel.RepositoryList.AddRepository(new GitWizardRepository("C:/repos/alpha"), streaming: false);
        viewModel.RepositoryList.ApplyFilterAndGrouping();
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.RepositoryList.Repositories, Is.Not.SameAs(previous));
            Assert.That(list.ItemsSource, Is.SameAs(viewModel.RepositoryList.Repositories));
            Assert.That(list.Items.Cast<RepositoryNodeViewModel>().Select(node => node.WorkingDirectory),
                Is.EqualTo(["C:/repos/alpha"]));
        });
    }

    [AvaloniaTest]
    public async Task FilterButtonActiveClass_FollowsRepositoryListActiveFilter()
    {
        await using var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        var button = window.FindControl<Button>("FilterStale")!;
        Dispatcher.UIThread.RunJobs();
        Assert.That(button.Classes.Contains("active"), Is.False);

        viewModel.RepositoryList.ToggleFilter(FilterType.Stale);
        Dispatcher.UIThread.RunJobs();
        Assert.That(button.Classes.Contains("active"), Is.True);

        viewModel.RepositoryList.ToggleFilter(FilterType.Stale);
        Dispatcher.UIThread.RunJobs();
        Assert.That(button.Classes.Contains("active"), Is.False);
    }
}
