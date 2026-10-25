using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Refresh tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryListViewModel.Rows.cs (AddRepository) and RefreshCoordinator.UiDrain.cs (DrainUICommands).
public class MainViewModelAddRepositoryTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel CreateViewModel() => TestViewModels.Create();

    static GitWizardRepository Repo(string workingDirectory) => new(workingDirectory);


    [TestCase("C:/repos/a", 1, TestName = "DrainedRepositoryCreated_ClearsIsScanning")]
    [TestCase("", 0, TestName = "DrainedRepositoryCreated_WithEmptyPath_StillClearsIsScanning")]
    public async Task DrainedRepositoryCreated_EndsScanning(string path, int expectedCount)
    {
        await using var viewModel = TestViewModels.Create(timeProvider: new ControlledDelayClock());
        viewModel.Refresh.IsScanning = true;
        viewModel.Refresh.OnRepositoryCreated(Repo(path));
        Assert.That(viewModel.Refresh.IsScanning, Is.True);

        viewModel.Refresh.DrainUICommands();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.Refresh.IsScanning, Is.False);
            Assert.That(viewModel.RepositoryList.AllRepositories, Has.Count.EqualTo(expectedCount));
            Assert.That(viewModel.RepositoryList.Repositories, Has.Count.EqualTo(expectedCount));
        }
    }

    [Test]
    public async Task RepositoryListAddRepository_DoesNotTouchIsScanning()
    {
        await using var viewModel = TestViewModels.Create(timeProvider: new ControlledDelayClock());
        viewModel.Refresh.IsScanning = true;

        viewModel.RepositoryList.AddRepository(Repo("C:/repos/a"), streaming: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.Refresh.IsScanning, Is.True);
            Assert.That(viewModel.RepositoryList.Repositories.Single().WorkingDirectory, Is.EqualTo("C:/repos/a"));
        }
    }

    [Test]
    public void AddRepository_AddsToRepositoriesWhenNoFilterOrGroup()
    {
        var vm = CreateViewModel();

        vm.RepositoryList.AddRepository(Repo("C:/repos/alpha"), streaming: false);
        vm.RepositoryList.AddRepository(Repo("C:/repos/beta"), streaming: false);

        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.RepositoryList.Repositories[0].WorkingDirectory, Is.EqualTo("C:/repos/alpha"));
            Assert.That(vm.RepositoryList.Repositories[1].WorkingDirectory, Is.EqualTo("C:/repos/beta"));
        }
    }

    [Test]
    public void AddRepository_SkipsRepoWithEmptyWorkingDirectory()
    {
        var vm = CreateViewModel();

        vm.RepositoryList.AddRepository(Repo(""), streaming: false);

        Assert.That(vm.RepositoryList.Repositories, Is.Empty,
            "A repository with an empty working directory must not appear in the list.");
    }

    [Test]
    public void AddRepository_SkipsRepoWithNullWorkingDirectory()
    {
        var vm = CreateViewModel();

        // GitWizardRepository(string) sets WorkingDirectory = the string argument;
        // a null path should be handled just like empty.
        vm.RepositoryList.AddRepository(Repo(null!), streaming: false);

        Assert.That(vm.RepositoryList.Repositories, Is.Empty);
    }

    [Test]
    public void AddRepository_RespectsActiveFilter_ExcludesNonMatchingRepo()
    {
        var vm = CreateViewModel();

        // PendingChanges filter requires HasPendingChanges = true; a plain repo does not match.
        vm.RepositoryList.ToggleFilter(FilterType.PendingChanges);
        vm.RepositoryList.AddRepository(Repo("C:/repos/clean"), streaming: false);

        Assert.That(vm.RepositoryList.Repositories, Is.Empty,
            "A repo that doesn't match the active filter must not appear.");
    }

    [Test]
    public void AddRepository_RespectsSearchText_ExcludesNonMatchingRepo()
    {
        var vm = CreateViewModel();
        vm.RepositoryList.SetSearchText("widget");

        vm.RepositoryList.AddRepository(Repo("C:/repos/alpha"), streaming: false);

        Assert.That(vm.RepositoryList.Repositories, Is.Empty,
            "A repo whose path doesn't contain the search text must be excluded.");
    }

    [Test]
    public void AddRepository_RespectsSearchText_IncludesMatchingRepo()
    {
        var vm = CreateViewModel();
        vm.RepositoryList.SetSearchText("alpha");

        vm.RepositoryList.AddRepository(Repo("C:/repos/alpha"), streaming: false);

        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void AddRepository_SearchTextIsCaseInsensitive()
    {
        var vm = CreateViewModel();
        vm.RepositoryList.SetSearchText("ALPHA");

        vm.RepositoryList.AddRepository(Repo("C:/repos/alpha"), streaming: false);

        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1),
            "Search text comparison must be case-insensitive.");
    }

    [Test]
    public void AddRepository_UsesGroupingWhenGroupModeActive()
    {
        var vm = CreateViewModel();
        vm.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        vm.RepositoryList.AddRepository(Repo("C:/repos/alpha"), streaming: false);

        // With Drive grouping, the repo goes into a group header, not directly into Repositories as a plain node.
        // The group header itself should be in Repositories (or in pending groups if minGroupSize not met).
        // For Drive mode, minGroupSize=1, so the header appears immediately.
        Assert.Multiple(() =>
        {
            Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1));
            Assert.That(vm.RepositoryList.Repositories[0].IsGroupHeader, Is.True,
                "With Drive grouping active, a group header must be added rather than the raw repo.");
            Assert.That(vm.RepositoryList.Repositories[0].Children, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void AddRepository_GroupMode_MultipleReposInSameGroup()
    {
        var vm = CreateViewModel();
        vm.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        vm.RepositoryList.AddRepository(Repo("C:/repos/alpha"), streaming: false);
        vm.RepositoryList.AddRepository(Repo("C:/repos/beta"), streaming: false);

        Assert.Multiple(() =>
        {
            Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1),
                "Both repos share the same drive group, so only one header should exist.");
            Assert.That(vm.RepositoryList.Repositories[0].Children, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void AddRepository_FilterAndSearchCombine()
    {
        var vm = CreateViewModel();

        // Set search text to "alpha" (only "alpha" paths pass the search).
        vm.RepositoryList.SetSearchText("alpha");
        // No filter active (FilterType.None), so MatchesFilter always returns true.

        vm.RepositoryList.AddRepository(Repo("C:/repos/alpha"), streaming: false);
        vm.RepositoryList.AddRepository(Repo("C:/repos/beta"), streaming: false);

        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1),
            "Only the repo matching the search text should appear.");
        Assert.That(vm.RepositoryList.Repositories[0].WorkingDirectory, Is.EqualTo("C:/repos/alpha"));
    }
}
