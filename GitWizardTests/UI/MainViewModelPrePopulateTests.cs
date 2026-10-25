using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Refresh tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryListViewModel.CachedRows.cs (PrePopulateFromReport).
public class MainViewModelPrePopulateTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel CreateViewModel() => TestViewModels.Create();


    [Test]
    public void PrePopulateFromReport_AddsCachedReposToUi_WhenNoFilterOrGroup()
    {
        using var sandbox = new LocalFilesSandbox();
        // Create a cached report with two repos.
        var report = new GitWizardReport();
        report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
        report.Repositories["/repos/beta"] = new GitWizardRepository("/repos/beta");
        report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

        // Also write the cached repo paths.
        GitWizardApi.SaveCachedRepositoryPaths(["/repos/alpha", "/repos/beta"]);

        var vm = CreateViewModel();

        // Pre-populate from the cached report.
        var config = new GitWizardConfiguration { SearchPaths = ["/repos"] };
        vm.RepositoryList.PrePopulateFromReport(config, ["/repos/alpha", "/repos/beta"], report);

        // Both repos should appear in the UI immediately.
        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(2),
            "Pre-population should add cached repos to Repositories.");
        Assert.That(vm.RepositoryList.AllRepositories.Count, Is.EqualTo(2),
            "Pre-population should add cached repos to AllRepositories.");

    }

    [Test]
    public void PrePopulateFromReport_SkipsReposNotInPathList()
    {
        using var sandbox = new LocalFilesSandbox();
        var report = new GitWizardReport();
        report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
        report.Repositories["/repos/beta"] = new GitWizardRepository("/repos/beta");
        report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

        // Only path "/repos/alpha" is in the repositoryPaths list.
        var vm = CreateViewModel();
        var config = new GitWizardConfiguration { SearchPaths = ["/repos"] };
        vm.RepositoryList.PrePopulateFromReport(config, ["/repos/alpha"], report);

        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1),
            "Only repos in the path list should be pre-populated.");
        Assert.That(vm.RepositoryList.Repositories[0].WorkingDirectory, Is.EqualTo("/repos/alpha"));

    }

    [Test]
    public void PrePopulateFromReport_NoOp_WhenNoCachedReport()
    {
        using var sandbox = new LocalFilesSandbox();
        // No cached report exists.
        var vm = CreateViewModel();
        var config = new GitWizardConfiguration { SearchPaths = ["/repos"] };
        vm.RepositoryList.PrePopulateFromReport(config, ["/repos/alpha"], null);

        Assert.That(vm.RepositoryList.Repositories, Is.Empty,
            "Pre-population should be a no-op when no cached report exists.");
        Assert.That(vm.RepositoryList.AllRepositories, Is.Empty);

    }

    [Test]
    public void PrePopulateFromReport_NoOp_WhenRepositoryPathsIsNull()
    {
        using var sandbox = new LocalFilesSandbox();
        var vm = CreateViewModel();
        var config = new GitWizardConfiguration { SearchPaths = ["/repos"] };
        vm.RepositoryList.PrePopulateFromReport(config, null!, null);

        Assert.That(vm.RepositoryList.Repositories, Is.Empty,
            "Pre-population should be a no-op when repositoryPaths is null.");

    }

    [Test]
    public void PrePopulateFromReport_GroupedViewHonorsActiveFilter()
    {
        using var sandbox = new LocalFilesSandbox();
        var report = new GitWizardReport();
        report.Repositories["C:/repos/alpha"] = new GitWizardRepository("C:/repos/alpha");
        report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

        var viewModel = CreateViewModel();
        viewModel.RepositoryList.ToggleFilter(FilterType.PendingChanges);
        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = ["C:/repos"]
        };

        viewModel.RepositoryList.PrePopulateFromReport(configuration, ["C:/repos/alpha"], report);

        Assert.That(viewModel.RepositoryList.Repositories, Is.Empty,
            "Grouped pre-population must not expose repositories excluded by the active filter.");

    }

    [Test]
    public void PrePopulateFromReport_EmptyNextCycle_DoesNotSuppressRediscoveredRepository()
    {
        using var sandbox = new LocalFilesSandbox();
        var report = new GitWizardReport();
        report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
        report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

        var viewModel = CreateViewModel();
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = ["/repos"]
        };
        viewModel.RepositoryList.PrePopulateFromReport(configuration, ["/repos/alpha"], report);
        Assert.That(viewModel.RepositoryList.RepositoryMap, Does.ContainKey("/repos/alpha"));

        viewModel.RepositoryList.ClearRepositories();
        viewModel.RepositoryList.PrePopulateFromReport(configuration, [], report);
        viewModel.RepositoryList.AddRepository(new GitWizardRepository("/repos/alpha"), streaming: false);

        Assert.That(viewModel.RepositoryList.Repositories, Has.Count.EqualTo(1),
            "A path cached in an earlier cycle must not suppress a fresh row.");

    }
}
