using GitWizard;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Refresh tests are split by concern because the combined fixture exceeds 500 lines.
// Covers MainViewModel.RefreshPrune.cs (PruneStalePrePopulatedRepos, RemoveRepositoryPathFromUi).
public class MainViewModelRefreshPruneTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel CreateViewModel() =>
        new(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

    static GitWizardRepository Repo(string workingDirectory) => new(workingDirectory);


    [Test]
    public void RemoveRepositoryPathFromUi_ExistingPath_RemovesFromRepositories()
    {
        var vm = CreateViewModel();
        vm.AddRepository(Repo("C:/repos/alpha"));
        vm.AddRepository(Repo("C:/repos/beta"));

        vm.RemoveRepositoryPathFromUi("C:/repos/alpha");

        Assert.That(vm.Repositories.Select(node => node.WorkingDirectory), Is.EquivalentTo(new[] { "C:/repos/beta" }));
    }

    [Test]
    public void RemoveRepositoryPathFromUi_UnknownPath_NoOp()
    {
        var vm = CreateViewModel();
        vm.AddRepository(Repo("C:/repos/alpha"));

        Assert.DoesNotThrow(() => vm.RemoveRepositoryPathFromUi("C:/repos/does-not-exist"));
        Assert.That(vm.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void PruneStalePrePopulatedRepos_RemovesFreshReplacementFromGroupedView()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            var report = new GitWizardReport();
            report.Repositories["C:/repos/alpha"] = new GitWizardRepository("C:/repos/alpha");
            report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

            var viewModel = CreateViewModel();
            viewModel.ToggleGroupMode(GroupMode.Drive);
            var configuration = new GitWizardConfiguration
            {
                SearchPaths = new SortedSet<string> { "C:/repos" }
            };
            viewModel.PrePopulateFromReport(configuration, ["C:/repos/alpha"], report);
            viewModel.AddRepository(new GitWizardRepository("C:/repos/alpha"));
            var groupHeader = viewModel.Repositories.Single(candidate => candidate.IsGroupHeader);

            viewModel.PruneStalePrePopulatedRepos([], []);

            Assert.That(viewModel._repositoryMap, Is.Empty);
            Assert.That(viewModel._allRepositories, Is.Empty);
            Assert.That(groupHeader.Children, Is.Empty,
                "Grouped views must not retain a fresh replacement for a rejected path.");
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public void RenamedPrePopulatedRepo_ReplacesOldPathWithNewPath()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            var report = new GitWizardReport();
            report.Repositories["/repos/old-alpha"] = new GitWizardRepository("/repos/old-alpha");
            report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

            var viewModel = CreateViewModel();
            var configuration = new GitWizardConfiguration
            {
                SearchPaths = new SortedSet<string> { "/repos" }
            };
            viewModel.PrePopulateFromReport(configuration, ["/repos/old-alpha"], report);
            viewModel.AddRepository(new GitWizardRepository("/repos/alpha"));

            viewModel.PruneStalePrePopulatedRepos(
                ["/repos/old-alpha", "/repos/alpha"],
                ["/repos/old-alpha"]);

            Assert.That(viewModel._repositoryMap.ContainsKey("/repos/old-alpha"), Is.False);
            Assert.That(viewModel._allRepositories.Select(node => node.WorkingDirectory),
                Is.EqualTo(new[] { "/repos/alpha" }));
            Assert.That(viewModel.Repositories.Select(node => node.WorkingDirectory),
                Is.EqualTo(new[] { "/repos/alpha" }));
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public void DeletedPrePopulatedRepo_DisappearsAfterRefresh()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            // Simulates a full RefreshAsync cycle where the cached report has two repos
            // but the fresh scan only discovers one (the other was deleted).
            // RefreshAsync clears _allRepositories / _repositoryMap before pre-populating,
            // so only fresh-scan repos survive.
            var report = new GitWizardReport();
            report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
            report.Repositories["/repos/beta"] = new GitWizardRepository("/repos/beta");
            report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

            var vm = CreateViewModel();
            var config = new GitWizardConfiguration { SearchPaths = new SortedSet<string> { "/repos" } };

            // Simulate RefreshAsync: clear backing collections, pre-populate from cache.
            vm._allRepositories.Clear();
            vm._repositoryMap.Clear();
            vm.Repositories.Clear();
            vm.PrePopulateFromReport(config, ["/repos/alpha", "/repos/beta"], report);
            Assert.That(vm._allRepositories, Has.Count.EqualTo(2),
                "Pre-condition: two repos pre-populated.");

            // RepositoryCreated is raised before refresh validation, so both paths can
            // receive fresh nodes even though beta is subsequently classified as deleted.
            var freshAlpha = new GitWizardRepository("/repos/alpha");
            vm.AddRepository(freshAlpha);
            vm.AddRepository(new GitWizardRepository("/repos/beta"));

            // beta is stale: its path is no longer in the validated fresh scan.
            // Pruning must remove the replacement node, not only the cached node identity.
            var freshPaths = new HashSet<string> { "/repos/alpha" };
            vm.PruneStalePrePopulatedRepos(freshPaths, new HashSet<string>());

            Assert.That(vm._repositoryMap.ContainsKey("/repos/beta"), Is.False,
                "A deleted pre-populated repo must be removed from _repositoryMap.");
            Assert.That(vm.Repositories, Has.Count.EqualTo(1),
                "A deleted pre-populated repo must be pruned from the visible collection.");
            Assert.That(vm.Repositories[0].WorkingDirectory, Is.EqualTo("/repos/alpha"),
                "The visible collection must only contain the fresh repo.");
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }
}
