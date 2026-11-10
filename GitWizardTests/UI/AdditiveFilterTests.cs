using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

/// <summary>
/// Sidebar filters are additive: every active filter narrows the list (intersection), and
/// clearing every filter shows everything.
/// </summary>
[NonParallelizable]
public class AdditiveFilterTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static RepositoryNodeViewModel Node(string path, int stashCount, int localOnlyBranchCount)
        => new(new GitWizardRepository(path) { StashCount = stashCount, LocalOnlyBranchCount = localOnlyBranchCount });

    static MainViewModel ViewModelWithThreeRepositories()
    {
        var viewModel = TestViewModels.Create();
        viewModel.RepositoryList.AddRepository(new GitWizardRepository("C:/repos/neither"), streaming: false);
        viewModel.RepositoryList.AddRepository(new GitWizardRepository("C:/repos/stash-only") { StashCount = 1 }, streaming: false);
        viewModel.RepositoryList.AddRepository(
            new GitWizardRepository("C:/repos/stash-and-local") { StashCount = 1, LocalOnlyBranchCount = 1 }, streaming: false);
        return viewModel;
    }

    static string[] VisibleNames(MainViewModel viewModel)
        => [.. viewModel.RepositoryList.Repositories.Select(row => Path.GetFileName(row.WorkingDirectory))];

    [Test]
    public void MatchesFilter_CombinedFlags_RequiresEveryFlag()
    {
        var stashOnly = Node("/a", stashCount: 1, localOnlyBranchCount: 0);
        var both = Node("/b", stashCount: 1, localOnlyBranchCount: 1);
        const FilterType combined = FilterType.Stashes | FilterType.LocalOnlyCommits;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stashOnly.MatchesFilter(FilterType.Stashes), Is.True);
            Assert.That(stashOnly.MatchesFilter(combined), Is.False, "A repository missing one active filter must be excluded.");
            Assert.That(both.MatchesFilter(combined), Is.True);
        }
    }

    [Test]
    public void ToggleFilter_TwoFilters_BothStayActive()
    {
        var viewModel = TestViewModels.Create();

        viewModel.RepositoryList.ToggleFilter(FilterType.Stashes);
        viewModel.RepositoryList.ToggleFilter(FilterType.LocalOnlyCommits);

        Assert.That(viewModel.RepositoryList.ActiveFilter, Is.EqualTo(FilterType.Stashes | FilterType.LocalOnlyCommits));
    }

    [Test]
    public void ApplyFilters_NoFilters_ShowsEverything()
    {
        var viewModel = ViewModelWithThreeRepositories();

        Assert.That(VisibleNames(viewModel), Is.EquivalentTo(["neither", "stash-only", "stash-and-local"]));
    }

    [Test]
    public void ApplyFilters_OneFilter_BehavesAsBefore()
    {
        var viewModel = ViewModelWithThreeRepositories();

        viewModel.RepositoryList.ToggleFilter(FilterType.Stashes);

        Assert.That(VisibleNames(viewModel), Is.EquivalentTo(["stash-only", "stash-and-local"]));
    }

    [Test]
    public void ApplyFilters_TwoFilters_NarrowsToIntersection()
    {
        var viewModel = ViewModelWithThreeRepositories();

        viewModel.RepositoryList.ToggleFilter(FilterType.Stashes);
        viewModel.RepositoryList.ToggleFilter(FilterType.LocalOnlyCommits);

        Assert.That(VisibleNames(viewModel), Is.EqualTo(["stash-and-local"]));
    }

    [Test]
    public void ToggleFilter_TurningOneOffAgain_WidensTheList()
    {
        var viewModel = ViewModelWithThreeRepositories();
        viewModel.RepositoryList.ToggleFilter(FilterType.Stashes);
        viewModel.RepositoryList.ToggleFilter(FilterType.LocalOnlyCommits);

        viewModel.RepositoryList.ToggleFilter(FilterType.LocalOnlyCommits);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.RepositoryList.ActiveFilter, Is.EqualTo(FilterType.Stashes));
            Assert.That(VisibleNames(viewModel), Is.EquivalentTo(["stash-only", "stash-and-local"]));
        }
    }

    [Test]
    public void ToggleFilter_TurningEveryFilterOff_ShowsEverythingAgain()
    {
        var viewModel = ViewModelWithThreeRepositories();
        viewModel.RepositoryList.ToggleFilter(FilterType.Stashes);
        viewModel.RepositoryList.ToggleFilter(FilterType.LocalOnlyCommits);

        viewModel.RepositoryList.ToggleFilter(FilterType.Stashes);
        viewModel.RepositoryList.ToggleFilter(FilterType.LocalOnlyCommits);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.RepositoryList.ActiveFilter, Is.EqualTo(FilterType.None));
            Assert.That(VisibleNames(viewModel), Has.Length.EqualTo(3));
        }
    }
}
