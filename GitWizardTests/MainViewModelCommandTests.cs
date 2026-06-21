using GitWizard;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// Covers the repository-action command bodies in <c>MainViewModel.Commands</c> that don't launch
/// external processes: the OpenInFork validation guards (which surface alerts) and
/// CleanDownstreamBranchesAsync deleting merged branches via the git CLI on a real temp repo.
/// </summary>
public class MainViewModelCommandTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static RepositoryNodeViewModel Node(string workingDirectory)
        => new(new GitWizardRepository(workingDirectory));

    [Test]
    public void OpenInFork_InvalidRepositoryPath_ShowsError()
    {
        var dialogs = new StubUserDialogs();
        var viewModel = new MainViewModel(new StubUiDispatcher(), dialogs, new StubClipboardService());

        viewModel.OpenInForkCommand.Execute(Node("/no/such/directory/xyz"));

        Assert.That(dialogs.AlertCalls.Any(a => a.Message.Contains("Invalid repository path")), Is.True,
            "A working directory that doesn't exist must surface the invalid-path alert.");
    }

    [Test]
    public void OpenInFork_NullNode_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var viewModel = new MainViewModel(new StubUiDispatcher(), dialogs, new StubClipboardService());

        viewModel.OpenInForkCommand.Execute(null);

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
        var viewModel = new MainViewModel(new StubUiDispatcher(), dialogs, new StubClipboardService());

        await viewModel.CleanDownstreamBranchesAsync(node);

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
        var viewModel = new MainViewModel(new StubUiDispatcher(), dialogs, new StubClipboardService());

        await viewModel.CleanDownstreamBranchesAsync(node);

        Assert.That(dialogs.AlertCalls, Is.Empty,
            "With no merged downstream branches there's nothing to confirm or delete.");
    }
}
