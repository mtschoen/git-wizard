using GitWizard;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Refresh tests are split by concern because the combined fixture exceeds 500 lines.
// Covers MainViewModel.Refresh.cs (AddRepository).
public class MainViewModelAddRepositoryTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel CreateViewModel() =>
        new(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

    static GitWizardRepository Repo(string workingDirectory) => new(workingDirectory);


    [Test]
    public void AddRepository_SetsIsScanningToFalse()
    {
        var vm = CreateViewModel();
        vm.IsScanning = true;

        vm.AddRepository(Repo("C:/repos/alpha"));

        Assert.That(vm.IsScanning, Is.False,
            "The first repo surfacing must end the scanning indicator.");
    }

    [Test]
    public void AddRepository_AddsToRepositoriesWhenNoFilterOrGroup()
    {
        var vm = CreateViewModel();

        vm.AddRepository(Repo("C:/repos/alpha"));
        vm.AddRepository(Repo("C:/repos/beta"));

        Assert.That(vm.Repositories, Has.Count.EqualTo(2));
        Assert.That(vm.Repositories[0].WorkingDirectory, Is.EqualTo("C:/repos/alpha"));
        Assert.That(vm.Repositories[1].WorkingDirectory, Is.EqualTo("C:/repos/beta"));
    }

    [Test]
    public void AddRepository_SkipsRepoWithEmptyWorkingDirectory()
    {
        var vm = CreateViewModel();

        vm.AddRepository(Repo(""));

        Assert.That(vm.Repositories, Is.Empty,
            "A repository with an empty working directory must not appear in the list.");
    }

    [Test]
    public void AddRepository_SkipsRepoWithNullWorkingDirectory()
    {
        var vm = CreateViewModel();

        // GitWizardRepository(string) sets WorkingDirectory = the string argument;
        // a null path should be handled just like empty.
        vm.AddRepository(Repo(null!));

        Assert.That(vm.Repositories, Is.Empty);
    }

    [Test]
    public void AddRepository_RespectsActiveFilter_ExcludesNonMatchingRepo()
    {
        var vm = CreateViewModel();

        // PendingChanges filter requires HasPendingChanges = true; a plain repo does not match.
        vm.ToggleFilter(FilterType.PendingChanges);
        vm.AddRepository(Repo("C:/repos/clean"));

        Assert.That(vm.Repositories, Is.Empty,
            "A repo that doesn't match the active filter must not appear.");
    }

    [Test]
    public void AddRepository_RespectsSearchText_ExcludesNonMatchingRepo()
    {
        var vm = CreateViewModel();
        vm.SetSearchText("widget");

        vm.AddRepository(Repo("C:/repos/alpha"));

        Assert.That(vm.Repositories, Is.Empty,
            "A repo whose path doesn't contain the search text must be excluded.");
    }

    [Test]
    public void AddRepository_RespectsSearchText_IncludesMatchingRepo()
    {
        var vm = CreateViewModel();
        vm.SetSearchText("alpha");

        vm.AddRepository(Repo("C:/repos/alpha"));

        Assert.That(vm.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void AddRepository_SearchTextIsCaseInsensitive()
    {
        var vm = CreateViewModel();
        vm.SetSearchText("ALPHA");

        vm.AddRepository(Repo("C:/repos/alpha"));

        Assert.That(vm.Repositories, Has.Count.EqualTo(1),
            "Search text comparison must be case-insensitive.");
    }

    [Test]
    public void AddRepository_UsesGroupingWhenGroupModeActive()
    {
        var vm = CreateViewModel();
        vm.ToggleGroupMode(GroupMode.Drive);

        vm.AddRepository(Repo("C:/repos/alpha"));

        // With Drive grouping, the repo goes into a group header, not directly into Repositories as a plain node.
        // The group header itself should be in Repositories (or in pending groups if minGroupSize not met).
        // For Drive mode, minGroupSize=1, so the header appears immediately.
        Assert.Multiple(() =>
        {
            Assert.That(vm.Repositories, Has.Count.EqualTo(1));
            Assert.That(vm.Repositories[0].IsGroupHeader, Is.True,
                "With Drive grouping active, a group header must be added rather than the raw repo.");
            Assert.That(vm.Repositories[0].Children, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void AddRepository_GroupMode_MultipleReposInSameGroup()
    {
        var vm = CreateViewModel();
        vm.ToggleGroupMode(GroupMode.Drive);

        vm.AddRepository(Repo("C:/repos/alpha"));
        vm.AddRepository(Repo("C:/repos/beta"));

        Assert.Multiple(() =>
        {
            Assert.That(vm.Repositories, Has.Count.EqualTo(1),
                "Both repos share the same drive group, so only one header should exist.");
            Assert.That(vm.Repositories[0].Children, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void AddRepository_StillSetsIsScanningFalse_EvenWhenRepoIsSkipped()
    {
        var vm = CreateViewModel();
        vm.IsScanning = true;

        // Empty path causes early return, but IsScanning is set BEFORE the path check.
        vm.AddRepository(Repo(""));

        Assert.That(vm.IsScanning, Is.False,
            "IsScanning must flip even when the repo itself is skipped.");
    }

    [Test]
    public void AddRepository_FilterAndSearchCombine()
    {
        var vm = CreateViewModel();

        // Set search text to "alpha" (only "alpha" paths pass the search).
        vm.SetSearchText("alpha");
        // No filter active (FilterType.None), so MatchesFilter always returns true.

        vm.AddRepository(Repo("C:/repos/alpha"));
        vm.AddRepository(Repo("C:/repos/beta"));

        Assert.That(vm.Repositories, Has.Count.EqualTo(1),
            "Only the repo matching the search text should appear.");
        Assert.That(vm.Repositories[0].WorkingDirectory, Is.EqualTo("C:/repos/alpha"));
    }
}
