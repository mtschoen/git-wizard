using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using GitWizardUI.Views;
using LibGit2Sharp;

namespace GitWizardTests;

public class RepositoryIndexRefreshViewModelTests
{
    [AvaloniaTest]
    public async Task Row_WarningAndContextActionFollowFlagChanges()
    {
        await using var window = new MainWindow();
        var list = window.FindControl<ListBox>("RepositoryList")!;
        var repository = new GitWizardRepository("repository");
        var node = new RepositoryNodeViewModel(repository);
        var row = (Border)list.ItemTemplate!.Build(node)!;
        row.DataContext = node;
        var host = new Window { Width = 1000, Height = 80, Content = row };
        try
        {
            host.Show();
            row.ContextMenu!.DataContext = node;
            Dispatcher.UIThread.RunJobs();
            var marker = row.GetVisualDescendants().OfType<TextBlock>()
                .Single(label => label.Name == "IndexRefreshWarning");
            var action = row.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Refresh index");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(marker.IsVisible, Is.False);
                Assert.That(action.IsVisible, Is.False);
            }

            repository.IndexRefreshReason = RepositoryIndexRefreshReason.DeadIndexLock;
            node.Update();
            Dispatcher.UIThread.RunJobs();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(marker.IsVisible, Is.True);
                Assert.That(ToolTip.GetTip(marker), Does.Contain("10 minutes"));
                Assert.That(action.IsVisible, Is.True);
                Assert.That(action.Tag, Is.SameAs(node));
            }

            repository.IndexRefreshReason = null;
            node.Update();
            Dispatcher.UIThread.RunJobs();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(marker.IsVisible, Is.False);
                Assert.That(action.IsVisible, Is.False);
            }
        }
        finally
        {
            host.Close();
        }
    }

    [TestCase(null, false, null)]
    [TestCase(RepositoryIndexRefreshReason.DeadIndexLock, true, "10 minutes")]
    [TestCase(RepositoryIndexRefreshReason.StaleIndex, true, "stale cached file information")]
    public void Row_OffersActionOnlyForWarning(RepositoryIndexRefreshReason? reason, bool expected, string? explanation)
    {
        var repository = new GitWizardRepository("repository") { IndexRefreshReason = reason };
        var node = new RepositoryNodeViewModel(repository);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.CanRefreshIndex, Is.EqualTo(expected));
            Assert.That(node.IndexRefreshTooltip, explanation == null ? Is.Null : Does.Contain(explanation));
            Assert.That(RepositoryNodeViewModel.CreateGroupHeader("group").CanRefreshIndex, Is.False);
        }
        repository.IndexRefreshReason = null;
        node.Update();
        Assert.That(node.CanRefreshIndex, Is.False);
    }

    [Test]
    public async Task Action_PreservesPossibleWriterLockAndShowsReasonOnRow_Issue482()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var library = new Repository(fixture.Path);
        var lockPath = Path.Combine(library.Info.Path, "index.lock");
        await File.WriteAllTextAsync(lockPath, "live writer");
        File.SetLastWriteTimeUtc(lockPath, FrozenCacheClock.Now.AddHours(-1).UtcDateTime);
        var repository = new GitWizardRepository(fixture.Path)
        {
            IndexRefreshReason = RepositoryIndexRefreshReason.DeadIndexLock
        };
        var node = new RepositoryNodeViewModel(repository);
        await using var viewModel = TestViewModels.Create(timeProvider: new FrozenCacheClock());
        viewModel.Commands.IndexRefreshService = new RepositoryIndexRefreshService(new FrozenCacheClock())
        {
            GitProcessRunning = () => true
        };

        await viewModel.Commands.RefreshIndexAsync(node);

        var lockContents = await File.ReadAllTextAsync(lockPath);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lockContents, Is.EqualTo("live writer"));
            Assert.That(node.CanRefreshIndex, Is.True);
            Assert.That(node.Status, Is.EqualTo(RefreshStatus.Error));
            Assert.That(node.StatusTooltip, Does.Contain("a git process may be using this repository"));
        }
    }

    [Test]
    public async Task Action_RefreshesFlaggedRepositoryAndUpdatesRow()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var library = new Repository(fixture.Path);
        var lockPath = Path.Combine(library.Info.Path, "index.lock");
        await File.WriteAllTextAsync(lockPath, string.Empty);
        File.SetLastWriteTimeUtc(lockPath, FrozenCacheClock.Now.AddHours(-1).UtcDateTime);
        var repository = new GitWizardRepository(fixture.Path);
        repository.RefreshPendingChangesStatus(library, false, timeProvider: new FrozenCacheClock());
        var node = new RepositoryNodeViewModel(repository);
        await using var viewModel = TestViewModels.Create(timeProvider: new FrozenCacheClock());
        viewModel.Commands.IndexRefreshService = new RepositoryIndexRefreshService(new FrozenCacheClock())
        {
            GitProcessRunning = () => false
        };

        await viewModel.Commands.RefreshIndexAsync(node);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(lockPath), Is.False);
            Assert.That(node.CanRefreshIndex, Is.False);
            Assert.That(node.Status, Is.EqualTo(RefreshStatus.Success));
        }
    }
}
