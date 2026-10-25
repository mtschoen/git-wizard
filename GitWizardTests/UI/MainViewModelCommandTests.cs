using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// Covers the repository-action methods in <c>RepositoryCommandsViewModel.cs</c> and cache actions
/// in <c>IndexCacheViewModel.cs</c>: clipboard actions, OpenInExplorer, DeepRefreshRepository,
/// CheckoutMatchingBranch, OpenInFork, CleanDownstreamBranches, and ClearCacheAsync.
/// </summary>
public class MainViewModelCommandTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static RepositoryNodeViewModel Node(string workingDirectory)
        => new(new GitWizardRepository(workingDirectory));

    [Test]
    public async Task CopyToClipboardAsync_WritesToClipboardAndLightsJustCopied()
    {
        var clock = new ControlledDelayClock();
        var clipboard = new StubClipboardService();
        var vm = TestViewModels.Create(clipboard: clipboard, timeProvider: clock);
        var node = Node("C:/projects/widget");
        var copy = vm.Commands.CopyToClipboardAsync(node);
        try
        {
            Assert.That(clock.Timer, Is.Not.Null);
            Assert.That(node.JustCopied, Is.True);
            Assert.That(clipboard.Writes, Does.Contain("C:/projects/widget"));
            Assert.That(copy.IsCompleted, Is.False);
            clock.Timer!.Fire();
            await copy;
            Assert.That(node.JustCopied, Is.False);
        }
        finally
        {
            clock.Timer?.Fire();
            await copy;
        }
    }

    [Test]
    public async Task CopyToClipboardAsync_EmptyWorkingDirectory_DoesNothing()
    {
        var clipboard = new StubClipboardService();
        var vm = TestViewModels.Create(clipboard: clipboard);
        var node = Node("");

        await vm.Commands.CopyToClipboardAsync(node);

        Assert.Multiple(() =>
        {
            Assert.That(clipboard.Writes, Is.Empty);
            Assert.That(node.JustCopied, Is.False);
        });
    }

    [Test]
    public void OpenInExplorer_NullNode_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var vm = TestViewModels.Create(dialogs: dialogs);

        vm.Commands.OpenInExplorer(null);

        Assert.That(dialogs.AlertCalls, Is.Empty);
    }

    [Test]
    public void OpenInExplorer_EmptyWorkingDirectory_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var vm = TestViewModels.Create(dialogs: dialogs);
        var node = Node("");

        vm.Commands.OpenInExplorer(node);

        Assert.That(dialogs.AlertCalls, Is.Empty);
    }

    [Test]
    public void DeepRefreshRepository_NullNode_DoesNothing()
    {
        var vm = TestViewModels.Create();
        vm.Commands.DeepRefreshRepository(null);
    }

    [Test]
    public void DeepRefreshRepository_GroupHeaderNode_DoesNothing()
    {
        var vm = TestViewModels.Create();
        var header = RepositoryNodeViewModel.CreateGroupHeader("C:\\");

        vm.Commands.DeepRefreshRepository(header);

        Assert.That(header.Status, Is.EqualTo(RefreshStatus.Success));
    }

    [Test]
    public async Task DeepRefreshRepository_SetsStatusToRefreshing()
    {
        var clock = new ControlledDelayClock();
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        await using var viewModel = TestViewModels.Create(timeProvider: clock);
        var repository = new GitWizardRepository(fixture.Path) { RefreshTimeSeconds = double.NaN };
        viewModel.RepositoryList.AddRepository(repository, streaming: false);
        var node = viewModel.RepositoryList.Repositories.Single();
        node.Status = RefreshStatus.Success;
        viewModel.Commands.DeepRefreshRepository(node);
        try
        {
            Assert.That(node.Status, Is.EqualTo(RefreshStatus.Refreshing));
        }
        finally
        {
            await AsyncCondition.WaitUntilAsync(() => !double.IsNaN(repository.RefreshTimeSeconds));
            clock.Timer?.Fire();
        }
    }

    [Test]
    public void CheckoutMatchingBranch_NullNode_DoesNothing()
    {
        var vm = TestViewModels.Create();
        vm.Commands.CheckoutMatchingBranch(null);
    }

    [Test]
    public void CheckoutMatchingBranch_GroupHeader_DoesNothing()
    {
        var vm = TestViewModels.Create();
        var header = RepositoryNodeViewModel.CreateGroupHeader("C:\\");

        vm.Commands.CheckoutMatchingBranch(header);

        Assert.That(header.IsGroupHeader, Is.True);
    }

    [Test]
    public void CheckoutMatchingBranch_EmptyMatchingBranch_DoesNothing()
    {
        var vm = TestViewModels.Create();
        var node = Node("C:/repos/alpha");

        vm.Commands.CheckoutMatchingBranch(node);

        Assert.That(node.Status, Is.EqualTo(RefreshStatus.Refreshing));
    }

    [Test]
    public async Task CleanDownstreamBranches_NullNode_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var vm = TestViewModels.Create(dialogs: dialogs);

        await vm.Commands.CleanDownstreamBranchesAsync(null);

        Assert.That(dialogs.AlertCalls, Is.Empty);
    }

    [Test]
    public async Task CleanDownstreamBranches_GroupHeader_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var vm = TestViewModels.Create(dialogs: dialogs);
        var header = RepositoryNodeViewModel.CreateGroupHeader("C:\\");

        await vm.Commands.CleanDownstreamBranchesAsync(header);

        Assert.That(dialogs.AlertCalls, Is.Empty);
    }

    [Test]
    public async Task CleanDownstreamBranches_DeletesMergedBranches()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("feature", "feature.txt");
        fixture.MergeBranchNoFastForward("feature");
        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        var node = new RepositoryNodeViewModel(repository);

        var dialogs = new StubUserDialogs();
        var viewModel = TestViewModels.Create(dialogs: dialogs);

        await viewModel.Commands.CleanDownstreamBranchesAsync(node);

        using var library = new Repository(fixture.Path);
        Assert.Multiple(() =>
        {
            Assert.That(library.Branches["feature"], Is.Null, "The merged downstream branch must be deleted.");
            Assert.That(dialogs.AlertCalls.Any(a => a.Message.Contains("Deleted")), Is.True,
                "A successful cleanup must report how many branches were deleted.");
        });
    }

    [Test]
    public async Task CleanDownstreamBranches_NoMergedBranches_DoesNothing()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        var node = new RepositoryNodeViewModel(repository);

        var dialogs = new StubUserDialogs();
        var viewModel = TestViewModels.Create(dialogs: dialogs);

        await viewModel.Commands.CleanDownstreamBranchesAsync(node);

        Assert.That(dialogs.AlertCalls, Is.Empty,
            "With no merged downstream branches there's nothing to confirm or delete.");
    }

    [Test]
    public async Task OpenInFork_InvalidRepositoryPath_ShowsError()
    {
        var dialogs = new StubUserDialogs();
        var viewModel = TestViewModels.Create(dialogs: dialogs);

        await viewModel.Commands.OpenInForkAsync(Node("/no/such/directory/xyz"));

        Assert.That(dialogs.AlertCalls.Any(a => a.Message.Contains("Invalid repository path")), Is.True,
            "A working directory that doesn't exist must surface the invalid-path alert.");
    }

    [Test]
    public async Task OpenInFork_NullNode_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var viewModel = TestViewModels.Create(dialogs: dialogs);

        await viewModel.Commands.OpenInForkAsync(null);

        Assert.That(dialogs.AlertCalls, Is.Empty);
    }

    [Test]
    public async Task OpenInFork_EmptyWorkingDirectory_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var vm = TestViewModels.Create(dialogs: dialogs);

        await vm.Commands.OpenInForkAsync(Node(""));

        Assert.That(dialogs.AlertCalls, Is.Empty);
    }

    [Test]
    public async Task OpenInFork_GroupHeader_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var vm = TestViewModels.Create(dialogs: dialogs);

        await vm.Commands.OpenInForkAsync(RepositoryNodeViewModel.CreateGroupHeader("C:\\"));

        Assert.That(dialogs.AlertCalls, Is.Empty);
    }

    [Test]
    public async Task OpenInFork_ConfiguredForkPathWithEnvironmentVariable_IsExpandedBeforeUse()
    {
        using var sandbox = new LocalFilesSandbox();
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var expected = Path.Combine(profile, "NoSuchTool", "tool.exe");
            var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync();
            configuration.ForkPath = Path.Combine("~", "NoSuchTool", "tool.exe");

            var dialogs = new StubUserDialogs();
            var vm = TestViewModels.Create(dialogs: dialogs);
            using var directory = new TempDirectory();
            var workingDirectory = directory.Path;
            await vm.Commands.OpenInForkAsync(Node(workingDirectory));

            Assert.That(dialogs.AlertCalls.Any(a => a.Message.Contains(expected, StringComparison.Ordinal)),
                Is.True);
        }
        finally
        {
            TestUtilities.ResetStaticCaches();
        }
    }

    [Test]
    public async Task ClearCacheAsync_CompletesWithoutError()
    {
        using var sandbox = new LocalFilesSandbox();
        var dialogs = new StubUserDialogs();
        await using var viewModel = TestViewModels.Create(dialogs: dialogs);
        await viewModel.IndexCache.ClearCacheAsync();
        Assert.That(dialogs.AlertCalls, Is.EqualTo([
            new StubUserDialogs.AlertCall("Cache Cleared", "Repository cache has been cleared")
        ]));
    }
}
