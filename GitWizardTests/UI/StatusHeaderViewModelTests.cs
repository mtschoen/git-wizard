using GitWizard;
using GitWizardUI.ViewModels;

namespace GitWizardTests.UI;

// Covers StatusHeaderViewModel.cs: header precedence and component notifications.
public class StatusHeaderViewModelTests
{
    static RepositoryNodeViewModel[] Rows(int count) => [.. Enumerable.Range(0, count).Select(number => new RepositoryNodeViewModel(new GitWizardRepository($"/header/repository-{number}")))];

    [Test]
    public void ShowFilterInfo_NoFilterNoGroup_PrefersLastRefreshMessage()
    {
        var header = new StatusHeaderViewModel
        {
            LastRefreshMessage = "Refresh completed in 1.00 seconds"
        };
        header.ShowRefreshMessage("Refreshing...");
        header.ShowLiveMessage("Live watch active");
        Assert.That(header.LastRefreshMessage, Is.EqualTo("Refresh completed in 1.00 seconds"));
        header.ShowFilterInfo(FilterType.None, GroupMode.None, 3, Rows(3));
        Assert.That(header.HeaderText, Is.EqualTo("Refresh completed in 1.00 seconds"));
    }

    [Test]
    public void ShowFilterInfo_NoFilterNoGroup_WithoutRefreshMessage_CountsRepositories()
    {
        var header = new StatusHeaderViewModel();
        header.ShowFilterInfo(FilterType.None, GroupMode.None, 3, Rows(3));
        Assert.That(header.HeaderText, Is.EqualTo("3 repositories"));
    }

    [TestCase(FilterType.None)]
    [TestCase(FilterType.PendingChanges)]
    public void ShowFilterInfo_Grouped_CountsOnlyGroupHeaders(FilterType filter)
    {
        var header = new StatusHeaderViewModel { LastRefreshMessage = "Refresh completed in 1.00 seconds" };
        var children = Rows(3);
        var firstGroup = RepositoryNodeViewModel.CreateGroupHeader("C:");
        var secondGroup = RepositoryNodeViewModel.CreateGroupHeader("D:");
        firstGroup.Children.Add(children[0]);
        firstGroup.Children.Add(children[1]);
        secondGroup.Children.Add(children[2]);
        firstGroup.IsExpanded = true;
        secondGroup.IsExpanded = true;
        RepositoryNodeViewModel[] rows = [firstGroup, children[0], children[1], secondGroup, children[2]];
        header.ShowFilterInfo(filter, GroupMode.Drive, 5, rows);
        Assert.That(header.HeaderText, Is.EqualTo("5 repositories in 2 groups"));
    }

    [Test]
    public void ShowFilterInfo_FilteredUngrouped_ShowsVisibleOfTotal()
    {
        var header = new StatusHeaderViewModel { LastRefreshMessage = "Refresh completed in 1.00 seconds" };
        header.ShowFilterInfo(FilterType.PendingChanges, GroupMode.None, 7, Rows(2));
        Assert.That(header.HeaderText, Is.EqualTo("Showing 2 of 7 repositories"));
    }

    [Test]
    public void HeaderText_RaisesPropertyChangedOncePerChange()
    {
        var header = new StatusHeaderViewModel();
        Assert.That(header.HeaderText, Is.EqualTo("GitWizard"));
        var notifications = new List<string?>();
        header.PropertyChanged += (sender, arguments) =>
        {
            Assert.That(sender, Is.SameAs(header));
            notifications.Add(arguments.PropertyName);
        };
        header.ShowLiveMessage("Live watch active");
        header.ShowLiveMessage("Live watch active");
        Assert.That(notifications, Is.EqualTo([nameof(StatusHeaderViewModel.HeaderText)]));
        header.ShowRefreshMessage("Refreshing...");
        header.ShowRefreshMessage("Refreshing...");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.HeaderText, Is.EqualTo("Refreshing..."));
            Assert.That(notifications, Is.EqualTo([
                nameof(StatusHeaderViewModel.HeaderText), nameof(StatusHeaderViewModel.HeaderText)
            ]));
        }
    }
}
