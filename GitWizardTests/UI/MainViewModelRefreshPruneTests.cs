using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Refresh tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryListViewModel.CachedRows.cs (PruneStalePrePopulatedRepos, RemoveRepositoryPathFromUi).
public class MainViewModelRefreshPruneTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel CreateViewModel() => TestViewModels.Create();

    static GitWizardRepository Repo(string workingDirectory) => new(workingDirectory);


    [Test]
    public void RemoveRepositoryPathFromUi_ExistingPath_RemovesFromRepositories()
    {
        var vm = CreateViewModel();
        vm.RepositoryList.AddRepository(Repo("C:/repos/alpha"), streaming: false);
        vm.RepositoryList.AddRepository(Repo("C:/repos/beta"), streaming: false);

        vm.RepositoryList.RemoveRepositoryPathFromUi("C:/repos/alpha");

        Assert.That(vm.RepositoryList.Repositories.Select(node => node.WorkingDirectory), Is.EquivalentTo(new[] { "C:/repos/beta" }));
    }

    [Test]
    public void RemoveRepositoryPathFromUi_UnknownPath_NoOp()
    {
        var vm = CreateViewModel();
        vm.RepositoryList.AddRepository(Repo("C:/repos/alpha"), streaming: false);

        Assert.DoesNotThrow(() => vm.RepositoryList.RemoveRepositoryPathFromUi("C:/repos/does-not-exist"));
        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void PruneStalePrePopulatedRepos_RemovesFreshReplacementFromGroupedView()
    {
        using var sandbox = new LocalFilesSandbox();
        var report = new GitWizardReport();
        report.Repositories["C:/repos/alpha"] = new GitWizardRepository("C:/repos/alpha");
        report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

        var viewModel = CreateViewModel();
        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = new SortedSet<string> { "C:/repos" }
        };
        viewModel.RepositoryList.PrePopulateFromReport(configuration, ["C:/repos/alpha"], report);
        viewModel.RepositoryList.AddRepository(new GitWizardRepository("C:/repos/alpha"), streaming: false);
        var groupHeader = viewModel.RepositoryList.Repositories.Single(candidate => candidate.IsGroupHeader);

        viewModel.RepositoryList.PruneStalePrePopulatedRepos([], []);

        Assert.That(viewModel.RepositoryList.RepositoryMap, Is.Empty);
        Assert.That(viewModel.RepositoryList.AllRepositories, Is.Empty);
        Assert.That(groupHeader.Children, Is.Empty,
            "Grouped views must not retain a fresh replacement for a rejected path.");

    }

    [Test]
    public void RenamedPrePopulatedRepo_ReplacesOldPathWithNewPath()
    {
        using var sandbox = new LocalFilesSandbox();
        var report = new GitWizardReport();
        report.Repositories["/repos/old-alpha"] = new GitWizardRepository("/repos/old-alpha");
        report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

        var viewModel = CreateViewModel();
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = new SortedSet<string> { "/repos" }
        };
        viewModel.RepositoryList.PrePopulateFromReport(configuration, ["/repos/old-alpha"], report);
        viewModel.RepositoryList.AddRepository(new GitWizardRepository("/repos/alpha"), streaming: false);

        viewModel.RepositoryList.PruneStalePrePopulatedRepos(
            ["/repos/old-alpha", "/repos/alpha"],
            ["/repos/old-alpha"]);

        Assert.That(viewModel.RepositoryList.RepositoryMap.ContainsKey("/repos/old-alpha"), Is.False);
        Assert.That(viewModel.RepositoryList.AllRepositories.Select(node => node.WorkingDirectory),
            Is.EqualTo(new[] { "/repos/alpha" }));
        Assert.That(viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory),
            Is.EqualTo(new[] { "/repos/alpha" }));

    }

    [Test]
    public void DeletedPrePopulatedRepo_DisappearsAfterRefresh()
    {
        using var sandbox = new LocalFilesSandbox();
        // Simulates a full RefreshAsync cycle where the cached report has two repos
        // but the fresh scan only discovers one (the other was deleted).
        // RefreshAsync clears AllRepositories / RepositoryMap before pre-populating,
        // so only fresh-scan repos survive.
        var report = new GitWizardReport();
        report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
        report.Repositories["/repos/beta"] = new GitWizardRepository("/repos/beta");
        report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

        var vm = CreateViewModel();
        var config = new GitWizardConfiguration { SearchPaths = new SortedSet<string> { "/repos" } };

        // Simulate RefreshAsync: clear backing collections, pre-populate from cache.
        vm.RepositoryList.ClearRepositories();
        vm.RepositoryList.PrePopulateFromReport(config, ["/repos/alpha", "/repos/beta"], report);
        Assert.That(vm.RepositoryList.AllRepositories, Has.Count.EqualTo(2),
            "Pre-condition: two repos pre-populated.");

        // RepositoryCreated is raised before refresh validation, so both paths can
        // receive fresh nodes even though beta is subsequently classified as deleted.
        var freshAlpha = new GitWizardRepository("/repos/alpha");
        vm.RepositoryList.AddRepository(freshAlpha, streaming: false);
        vm.RepositoryList.AddRepository(new GitWizardRepository("/repos/beta"), streaming: false);

        // beta is stale: its path is no longer in the validated fresh scan.
        // Pruning must remove the replacement node, not only the cached node identity.
        var freshPaths = new HashSet<string> { "/repos/alpha" };
        vm.RepositoryList.PruneStalePrePopulatedRepos(freshPaths, new HashSet<string>());

        Assert.That(vm.RepositoryList.RepositoryMap.ContainsKey("/repos/beta"), Is.False,
            "A deleted pre-populated repo must be removed from RepositoryMap.");
        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1),
            "A deleted pre-populated repo must be pruned from the visible collection.");
        Assert.That(vm.RepositoryList.Repositories[0].WorkingDirectory, Is.EqualTo("/repos/alpha"),
            "The visible collection must only contain the fresh repo.");

    }
}
