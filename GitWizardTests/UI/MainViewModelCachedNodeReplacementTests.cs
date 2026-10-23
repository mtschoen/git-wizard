using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Refresh tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryListViewModel.Rows.cs and RepositoryListViewModel.CachedRows.cs (cached node replacement).
public class MainViewModelCachedNodeReplacementTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel CreateViewModel() => TestViewModels.Create();


    [Test]
    public void AddRepository_SkipsDuplicateRepositoriesAdd_ForPrePopulatedRepo()
    {
        using var sandbox = new LocalFilesSandbox();
        var report = new GitWizardReport();
        report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
        report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

        var vm = CreateViewModel();
        var config = new GitWizardConfiguration { SearchPaths = new SortedSet<string> { "/repos" } };
        vm.RepositoryList.PrePopulateFromReport(config, ["/repos/alpha"], report);

        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1), "Pre-condition: one repo pre-populated.");

        // Simulate OnRepositoryCreated firing during the refresh - same path.
        var freshRepo = new GitWizardRepository("/repos/alpha");
        vm.RepositoryList.AddRepository(freshRepo, streaming: false);

        // Should NOT have added a duplicate to Repositories.
        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1),
            "AddRepository must not duplicate repos that were pre-populated.");

        // The node in AllRepositories (which ApplyFilterAndGrouping uses to rebuild
        // Repositories) must be the fresh scan node, not the stale cached node.
        // If it were stale the user would see old branch / status data after the
        // grouping pass rebuilds the visible collection.
        var freshNode = vm.RepositoryList.RepositoryMap["/repos/alpha"];
        Assert.That(vm.RepositoryList.AllRepositories[0], Is.SameAs(freshNode),
            "AllRepositories must hold the fresh scan node so ApplyFilterAndGrouping " +
            "rebuilds Repositories with updated data.");

    }

    [Test]
    public void AddRepository_FreshScanNodeReplacesStaleCachedNode_InAllCollections()
    {
        using var sandbox = new LocalFilesSandbox();
        var report = new GitWizardReport();
        report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
        report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

        var vm = CreateViewModel();
        var config = new GitWizardConfiguration { SearchPaths = new SortedSet<string> { "/repos" } };
        vm.RepositoryList.PrePopulateFromReport(config, ["/repos/alpha"], report);

        var cachedNode = vm.RepositoryList.AllRepositories[0];
        Assert.That(vm.RepositoryList.RepositoryMap, Does.ContainKey("/repos/alpha"),
            "Pre-condition: repo is pre-populated.");

        // Simulate a fresh scan result for the same path.
        var freshRepo = new GitWizardRepository("/repos/alpha");
        vm.RepositoryList.AddRepository(freshRepo, streaming: false);

        // AllRepositories must contain the fresh node (not the cached one).
        Assert.That(vm.RepositoryList.AllRepositories, Has.Count.EqualTo(1),
            "AllRepositories must not grow on refresh.");
        Assert.That(vm.RepositoryList.AllRepositories[0], Is.Not.SameAs(cachedNode),
            "AllRepositories must hold the fresh scan node, not the stale cached node.");
        Assert.That(vm.RepositoryList.AllRepositories[0].Repository, Is.SameAs(freshRepo),
            "AllRepositories[0].Repository must be the fresh GitWizardRepository.");

        // Every collection must point to the fresh node immediately, before the
        // end-of-refresh ApplyFilterAndGrouping rebuild.
        var freshNode = vm.RepositoryList.RepositoryMap["/repos/alpha"];
        Assert.That(freshNode.Repository, Is.SameAs(freshRepo),
            "RepositoryMap must hold the fresh scan node.");
        Assert.That(vm.RepositoryList.Repositories[0], Is.SameAs(freshNode),
            "The visible row must be replaced as soon as the fresh scan node arrives.");

        vm.RepositoryList.UpdateCompletedRepository(freshRepo, streaming: false);

        Assert.That(vm.RepositoryList.Repositories[0].Status, Is.EqualTo(RefreshStatus.Success),
            "Completion updates must reach the visible fresh row.");

    }

    [Test]
    public void AddRepository_FreshScanNodeReplacesCachedNode_InGroupedView()
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

        var groupHeader = viewModel.RepositoryList.Repositories.Single(candidate => candidate.IsGroupHeader);
        var cachedNode = groupHeader.Children.Single();
        var freshRepository = new GitWizardRepository("C:/repos/alpha");

        viewModel.RepositoryList.AddRepository(freshRepository, streaming: false);

        var freshNode = viewModel.RepositoryList.RepositoryMap["C:/repos/alpha"];
        Assert.That(groupHeader.Children, Has.Count.EqualTo(1));
        Assert.That(groupHeader.Children[0], Is.SameAs(freshNode));
        Assert.That(groupHeader.Children[0], Is.Not.SameAs(cachedNode));

    }

    [Test]
    public void TwoConsecutiveRefreshes_DoesNotDuplicateRows()
    {
        using var sandbox = new LocalFilesSandbox();
        var report = new GitWizardReport();
        report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
        report.Repositories["/repos/beta"] = new GitWizardRepository("/repos/beta");
        report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));
        GitWizardApi.SaveCachedRepositoryPaths(["/repos/alpha", "/repos/beta"]);

        var vm = CreateViewModel();
        var config = new GitWizardConfiguration { SearchPaths = new SortedSet<string> { "/repos" } };

        // First "refresh" cycle.
        vm.RepositoryList.PrePopulateFromReport(config, ["/repos/alpha", "/repos/beta"], report);
        var freshAlpha1 = new GitWizardRepository("/repos/alpha");
        var freshBeta1 = new GitWizardRepository("/repos/beta");
        vm.RepositoryList.AddRepository(freshAlpha1, streaming: false);
        vm.RepositoryList.AddRepository(freshBeta1, streaming: false);

        Assert.That(vm.RepositoryList.AllRepositories, Has.Count.EqualTo(2),
            "Pre-condition: two repos after first refresh cycle.");

        // Second "refresh" cycle (simulates consecutive refresh).
        // RefreshAsync clears AllRepositories / RepositoryMap before pre-populating
        // so only nodes from the fresh scan survive.
        vm.RepositoryList.ClearRepositories();
        vm.RepositoryList.PrePopulateFromReport(config, ["/repos/alpha", "/repos/beta"], report);
        var freshAlpha2 = new GitWizardRepository("/repos/alpha");
        var freshBeta2 = new GitWizardRepository("/repos/beta");
        vm.RepositoryList.AddRepository(freshAlpha2, streaming: false);
        vm.RepositoryList.AddRepository(freshBeta2, streaming: false);

        Assert.That(vm.RepositoryList.AllRepositories, Has.Count.EqualTo(2),
            "Two consecutive refreshes must not duplicate rows in AllRepositories.");
        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(2),
            "Two consecutive refreshes must not duplicate rows in the visible collection.");

    }
}
