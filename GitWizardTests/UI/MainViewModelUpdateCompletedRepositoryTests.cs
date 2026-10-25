using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Refresh tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryListViewModel.Rows.cs (UpdateCompletedRepository).
public class MainViewModelUpdateCompletedRepositoryTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel CreateViewModel() => TestViewModels.Create();

    static GitWizardRepository Repo(string workingDirectory) => new(workingDirectory);


    [Test]
    public void UpdateCompletedRepository_CallsUpdateOnMatchingNode()
    {
        var vm = CreateViewModel();
        var repo = Repo("C:/repos/alpha");
        vm.RepositoryList.AddRepository(repo, streaming: false);

        // After AddRepository, the node starts as Refreshing. Simulate completion.
        var node = vm.RepositoryList.Repositories[0];
        Assert.That(node.Status, Is.EqualTo(RefreshStatus.Refreshing),
            "Precondition: newly added node starts as Refreshing.");

        vm.RepositoryList.UpdateCompletedRepository(repo, streaming: false);

        // Update() should change status from Refreshing to Success (no error, no submodule issues).
        Assert.That(node.Status, Is.EqualTo(RefreshStatus.Success),
            "Update must transition the node to its computed status.");
    }

    [Test]
    public void UpdateCompletedRepository_UnknownPath_IsNoOp()
    {
        var vm = CreateViewModel();
        vm.RepositoryList.AddRepository(Repo("C:/repos/alpha"), streaming: false);

        // This should not throw or alter the list.
        vm.RepositoryList.UpdateCompletedRepository(Repo("C:/repos/unknown"), streaming: false);

        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void UpdateCompletedRepository_EmptyPath_IsNoOp()
    {
        var vm = CreateViewModel();

        // Should not throw.
        vm.RepositoryList.UpdateCompletedRepository(Repo(""), streaming: false);

        Assert.That(vm.RepositoryList.Repositories, Is.Empty);
    }

    [Test]
    public void UpdateCompletedRepository_RemovesRepoWhenFilterNoLongerMatches()
    {
        var vm = CreateViewModel();

        // Use a real temp repo that has pending changes so it matches PendingChanges filter.
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddUntrackedFile("dirty.txt");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        // At this point repo.HasPendingChanges should be true. Add it with filter on.
        vm.RepositoryList.ToggleFilter(FilterType.PendingChanges);
        vm.RepositoryList.AddRepository(repo, streaming: false);
        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1), "Precondition: dirty repo passes filter.");

        // Now simulate: the pending file is resolved and a refresh clears the flag.
        // Delete the untracked file and refresh again.
        File.Delete(Path.Combine(fixture.Path, "dirty.txt"));
        repo.Refresh();

        vm.RepositoryList.UpdateCompletedRepository(repo, streaming: false);

        Assert.That(vm.RepositoryList.Repositories, Is.Empty,
            "After update, a repo that no longer matches the active filter must be removed.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UpdateCompletedRepository_AddsRepoWhenFilterNewlyMatches(bool streaming)
    {
        await using var viewModel = TestViewModels.Create();
        var repository = Repo("C:/repos/alpha");
        var laterRepository = Repo("C:/repos/zulu");
        laterRepository.HasPendingChanges = true;
        viewModel.RepositoryList.ToggleFilter(FilterType.PendingChanges);
        viewModel.RepositoryList.AddRepository(laterRepository, streaming: false);
        viewModel.RepositoryList.AddRepository(repository, streaming: false);
        Assert.That(viewModel.RepositoryList.Repositories.Single().WorkingDirectory, Is.EqualTo("C:/repos/zulu"));
        repository.HasPendingChanges = true;

        viewModel.RepositoryList.UpdateCompletedRepository(repository, streaming);

        var expectedPaths = streaming
            ? new[] { "C:/repos/zulu", "C:/repos/alpha" }
            : new[] { "C:/repos/alpha", "C:/repos/zulu" };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory), Is.EqualTo(expectedPaths));
            Assert.That(viewModel.StatusHeader.HeaderText, Is.EqualTo("Showing 2 of 2 repositories"));
        }
    }

    [Test]
    public void UpdateCompletedRepository_UpdatesGroupHeaders()
    {
        var vm = CreateViewModel();
        vm.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        var repo = Repo("C:/repos/alpha");
        vm.RepositoryList.AddRepository(repo, streaming: false);

        // The group header's display text includes child status counts.
        var header = vm.RepositoryList.Repositories[0];
        Assert.That(header.IsGroupHeader, Is.True, "Precondition.");

        // Set an error on the repo, then update.
        repo.RefreshError = "Something went wrong";
        vm.RepositoryList.UpdateCompletedRepository(repo, streaming: false);

        // After UpdateCompletedRepository, the header's display text should be refreshed.
        // (The child node.Update() marks it Error, and the group header shows error counts.)
        Assert.That(header.DisplayText, Does.Contain("error"),
            "Group header display text must reflect updated child error counts.");
    }

    [Test]
    public void UpdateCompletedRepository_WithRefreshError_FeedsErrorLog()
    {
        var vm = CreateViewModel();
        var repo = Repo("C:/repos/alpha");
        vm.RepositoryList.AddRepository(repo, streaming: false);

        repo.RefreshError = "Something went wrong";
        vm.RepositoryList.UpdateCompletedRepository(repo, streaming: false);

        Assert.Multiple(() =>
        {
            Assert.That(vm.ErrorLog.Count, Is.EqualTo(1));
            var entry = vm.ErrorLog.Entries.Single();
            Assert.That(entry.Message, Is.EqualTo("Something went wrong"));
            Assert.That(entry.RepositoryPath, Is.EqualTo("C:/repos/alpha"));
        });
    }

    [Test]
    public void UpdateCompletedRepository_WithoutRefreshError_DoesNotFeedErrorLog()
    {
        var vm = CreateViewModel();
        var repo = Repo("C:/repos/alpha");
        vm.RepositoryList.AddRepository(repo, streaming: false);

        vm.RepositoryList.UpdateCompletedRepository(repo, streaming: false);

        Assert.That(vm.ErrorLog.Count, Is.Zero);
    }
}
