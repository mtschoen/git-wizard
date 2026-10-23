using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

/// <summary>
/// Regression coverage for issue #241: Working Directory sort must explicitly order repository
/// paths (case-insensitively on Windows, since discovered paths are cached lowercase while
/// Live-created ones arrive in OS-native case), Group by Drive must not split a drive into
/// separate groups by path case, and the header's reported group count must match the number of
/// groups actually shown.
/// </summary>
// Covers RepositoryListViewModel.cs, RepositoryListViewModel.Groups.cs, RepositoryGroupKeys.cs, RepositorySortOrder.cs, and StatusHeaderViewModel.cs.
public class MainViewModelWorkingDirectorySortTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel NewViewModel() => TestViewModels.Create();

    static GitWizardRepository Repo(string workingDirectory) => new(workingDirectory);

    static readonly StringComparer PlatformComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    // Mixed drives, mixed case, and reverse arrival order: none of this is alphabetical as added.
    static readonly string[] MixedOrderPaths =
    [
        @"f:\repos\Zulu", @"C:\repos\Zulu",
        @"f:\repos\alpha", @"C:\repos\alpha",
    ];

    [Test]
    public void WorkingDirectory_ArrivalOrderIsSortedCaseInsensitively_Ungrouped()
    {
        var viewModel = NewViewModel();

        // ActiveSortMode already defaults to WorkingDirectory; no explicit ApplySort call.
        foreach (var path in MixedOrderPaths)
            viewModel.AddRepository(Repo(path));

        var expected = MixedOrderPaths.OrderBy(path => path, PlatformComparer).ToArray();
        var actual = viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory).ToArray();
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void WorkingDirectory_LiveArrivalInsertsAtSortedPosition_WithoutReselectingSort()
    {
        var viewModel = NewViewModel();
        foreach (var path in MixedOrderPaths)
            viewModel.AddRepository(Repo(path));

        // Simulates the final AddRepository call ApplyLiveCreatedAsync makes once a newly
        // created repository's refresh completes.
        const string arriving = @"C:\repos\Beta";
        viewModel.AddRepository(Repo(arriving));

        var expected = MixedOrderPaths.Append(arriving).OrderBy(path => path, PlatformComparer).ToArray();
        var actual = viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory).ToArray();
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void WorkingDirectory_PrePopulateFromReport_OrdersCachedRows()
    {
        var viewModel = NewViewModel();
        var report = new GitWizardReport();
        foreach (var path in MixedOrderPaths)
            report.Repositories[path] = Repo(path);

        viewModel.PrePopulateFromReport(new GitWizardConfiguration(), MixedOrderPaths, report);

        var expected = MixedOrderPaths.OrderBy(path => path, PlatformComparer).ToArray();
        var actual = viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory).ToArray();
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void WorkingDirectory_GroupedByDrive_ChildrenAreSortedAndArrivalInsertsInPlace()
    {
        var viewModel = NewViewModel();
        foreach (var path in MixedOrderPaths)
            viewModel.AddRepository(Repo(path));

        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        foreach (var header in viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader))
        {
            var children = header.Children.Select(n => n.WorkingDirectory).ToArray();
            Assert.That(children, Is.EqualTo(children.OrderBy(path => path, PlatformComparer)),
                $"Children of group '{header.GroupKey}' must be sorted.");
        }

        // Expand every header so a mid-alphabet arrival's insertion position is observable in
        // the flat displayed list (issue #241's evidence describes exactly this: sort must hold
        // for both ungrouped and grouped views, including as new rows arrive).
        var headers = viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader).ToList();
        foreach (var header in headers)
            viewModel.RepositoryList.ToggleGroupExpand(header);

        const string arriving = @"C:\repos\Beta";
        viewModel.AddRepository(Repo(arriving));

        var ownerHeader = headers.Single(h => h.Children.Any(c => c.WorkingDirectory == arriving));
        var ownerIndex = viewModel.RepositoryList.Repositories.IndexOf(ownerHeader);
        var flatChildren = viewModel.RepositoryList.Repositories
            .Skip(ownerIndex + 1)
            .TakeWhile(n => !n.IsGroupHeader)
            .Select(n => n.WorkingDirectory)
            .ToArray();
        Assert.That(flatChildren, Is.EqualTo(ownerHeader.Children.Select(n => n.WorkingDirectory)),
            "The flat expanded rows must mirror the header's own (now re-sorted) children in order.");
        Assert.That(flatChildren, Does.Contain(arriving));
    }

    [Test]
    public void GroupByDrive_MixedCasePaths_ProduceOneGroupPerDrive()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Drive-letter case ambiguity is a Windows-only concern.");

        var viewModel = NewViewModel();

        // Discovered repositories are cached with a lowercase drive letter; a repository created
        // during a Live watch arrives with the OS-native (here, uppercase) drive letter. Both must
        // land in the same "C:\" group, not split into "c:\" and "C:\" (issue #241 addendum).
        viewModel.AddRepository(Repo(@"c:\repos\alpha"));
        viewModel.AddRepository(Repo(@"c:\repos\beta"));
        viewModel.AddRepository(Repo(@"C:\repos\gamma"));

        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        var headers = viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader).ToList();
        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.That(headers[0].Children, Has.Count.EqualTo(3));
    }

    [Test]
    public void GroupByDrive_LiveArrivalWithDifferentCase_JoinsExistingGroup()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Drive-letter case ambiguity is a Windows-only concern.");

        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo(@"c:\repos\alpha"));
        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        // A later Live-created repository on the same drive, but with the OS-native uppercase
        // drive letter, must join the existing group rather than creating a second one.
        viewModel.AddRepository(Repo(@"C:\repos\beta"));

        var headers = viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader).ToList();
        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.That(headers[0].Children, Has.Count.EqualTo(2));
    }



    // The bulk path above is for whole-batch callers; a single Live/refresh arrival must still use
    // InsertRepositoryNode's per-row binary search rather than a linear scan.
    [Test]
    public void WorkingDirectory_IsolatedArrival_LandsAtSortedPositionWithOnePositionalInsert()
    {
        const int existingCount = 200;
        var viewModel = NewViewModel();
        var positionalInserts = new PositionalInsertRecorder(viewModel.RepositoryList.Repositories);
        var paths = Enumerable.Range(0, existingCount).Select(i => $@"C:\repos\r{i:D4}").ToArray();
        var report = new GitWizardReport();
        foreach (var path in paths)
            report.Repositories[path] = Repo(path);
        viewModel.PrePopulateFromReport(new GitWizardConfiguration(), paths, report);

        // Sorts strictly between "C:\repos\r0099" and "C:\repos\r0100" - a genuine mid-list
        // insert, not an append at either end.
        viewModel.AddRepository(Repo(@"C:\repos\r0099a"));

        Assert.That(viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory).ToArray(),
            Is.EqualTo(paths.Append(@"C:\repos\r0099a").OrderBy(p => p, PlatformComparer)),
            "A single isolated arrival must land at its sorted position.");

        // A single isolated arrival is exactly the case InsertRepositoryNode's own O(n) shift is
        // acceptable for - it is not part of a batch this fix is meant to speed up - so exactly
        // one positional insert is expected here (unlike the batch paths, which must stay at 0).
        Assert.That(positionalInserts.Count, Is.EqualTo(1));
    }

    // Guards against InsertRepositoryNode regressing to a linear scan. The comparer is a private
    // static, so the test observes the search from outside: it swaps the ObservableCollection's
    // backing list for one that counts indexer reads (the search reads one existing row per
    // comparison), then bounds the reads of a single mid-list arrival logarithmically.
    [Test]
    public void WorkingDirectory_IsolatedArrival_UsesBinarySearchNotLinearScan()
    {
        const int existingCount = 1000;
        var viewModel = NewViewModel();
        var paths = Enumerable.Range(0, existingCount).Select(i => $@"C:\repos\r{i:D4}").ToArray();
        var report = new GitWizardReport();
        foreach (var path in paths)
            report.Repositories[path] = Repo(path);
        viewModel.PrePopulateFromReport(new GitWizardConfiguration(), paths, report);

        var reads = CountingList.Install(viewModel.RepositoryList.Repositories);

        // Sorts strictly between "C:\repos\r0499" and "C:\repos\r0500": a genuine mid-list insert.
        viewModel.AddRepository(Repo(@"C:\repos\r0499a"));
        var searchReads = reads.IndexerReads;

        Assert.That(viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory).ToArray(),
            Is.EqualTo(paths.Append(@"C:\repos\r0499a").OrderBy(p => p, PlatformComparer)));

        // Binary search over 1000 rows needs at most ceil(log2(1001)) = 10 reads; a linear scan
        // needs about 500. 2 * 10 leaves headroom for incidental reads while ruling out a scan.
        Assert.That(searchReads, Is.GreaterThan(0), "The counting list must observe the search.");
        Assert.That(searchReads, Is.LessThanOrEqualTo(2 * 10),
            "A single isolated arrival must use a binary search, not a linear scan.");
    }













    [Test]
    public void HeaderGroupCount_MatchesGroupsShown_EvenWhenAGroupIsExpanded()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo(@"C:\r1"));
        viewModel.AddRepository(Repo(@"D:\r1"));
        viewModel.AddRepository(Repo(@"E:\r1"));

        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);
        var expectedGroupCount = viewModel.RepositoryList.Repositories.Count(n => n.IsGroupHeader);

        // Expanding a group used to inflate Repositories.Count with the header's own children
        // (they are inserted as flat rows for display), which UpdateHeaderWithFilterInfo then
        // mistook for the group count (issue #241 addendum: "953 repositories in 2 groups" while
        // six groups were listed).
        viewModel.RepositoryList.ToggleGroupExpand(viewModel.RepositoryList.Repositories.First(n => n.IsGroupHeader));

        Assert.That(viewModel.StatusHeader.HeaderText, Does.Contain($"in {expectedGroupCount} groups"));
    }


}
