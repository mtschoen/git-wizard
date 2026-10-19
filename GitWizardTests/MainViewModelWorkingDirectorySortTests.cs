using System.Collections.ObjectModel;
using System.Collections.Specialized;
using GitWizard;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

/// <summary>
/// Regression coverage for issue #241: Working Directory sort must explicitly order repository
/// paths (case-insensitively on Windows, since discovered paths are cached lowercase while
/// Live-created ones arrive in OS-native case), Group by Drive must not split a drive into
/// separate groups by path case, and the header's reported group count must match the number of
/// groups actually shown.
/// </summary>
public class MainViewModelWorkingDirectorySortTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel NewViewModel()
        => new(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

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
        var actual = viewModel.Repositories.Select(node => node.WorkingDirectory).ToArray();
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
        var actual = viewModel.Repositories.Select(node => node.WorkingDirectory).ToArray();
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
        var actual = viewModel.Repositories.Select(node => node.WorkingDirectory).ToArray();
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void WorkingDirectory_GroupedByDrive_ChildrenAreSortedAndArrivalInsertsInPlace()
    {
        var viewModel = NewViewModel();
        foreach (var path in MixedOrderPaths)
            viewModel.AddRepository(Repo(path));

        viewModel.ApplyGroup("GroupByDrive");

        foreach (var header in viewModel.Repositories.Where(n => n.IsGroupHeader))
        {
            var children = header.Children.Select(n => n.WorkingDirectory).ToArray();
            Assert.That(children, Is.EqualTo(children.OrderBy(path => path, PlatformComparer)),
                $"Children of group '{header.GroupKey}' must be sorted.");
        }

        // Expand every header so a mid-alphabet arrival's insertion position is observable in
        // the flat displayed list (issue #241's evidence describes exactly this: sort must hold
        // for both ungrouped and grouped views, including as new rows arrive).
        var headers = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();
        foreach (var header in headers)
            viewModel.ToggleGroupExpand(header);

        const string arriving = @"C:\repos\Beta";
        viewModel.AddRepository(Repo(arriving));

        var ownerHeader = headers.Single(h => h.Children.Any(c => c.WorkingDirectory == arriving));
        var ownerIndex = viewModel.Repositories.IndexOf(ownerHeader);
        var flatChildren = viewModel.Repositories
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

        viewModel.ApplyGroup("GroupByDrive");

        var headers = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();
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
        viewModel.ApplyGroup("GroupByDrive");

        // A later Live-created repository on the same drive, but with the OS-native uppercase
        // drive letter, must join the existing group rather than creating a second one.
        viewModel.AddRepository(Repo(@"C:\repos\beta"));

        var headers = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();
        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.That(headers[0].Children, Has.Count.EqualTo(2));
    }

    // pr-crew review of PR #241/#243: InsertRepositoryNode's per-row scan, called once per
    // pre-populated row, cost O(n) per row for O(n^2) total across a large cached report.
    // PrePopulateFromReport must instead sort the whole ungrouped batch once (SortBatchForDisplay)
    // and append it, for O(n log n) total. The recorder below observes the collection's own
    // change notifications, so a batch that took the old per-row path is distinguishable from one
    // that took the bulk path without asserting on wall-clock time.
    [Test]
    public void WorkingDirectory_PrePopulateFromReport_LargeBatch_SortsOnceInsteadOfPerRow()
    {
        const int count = 300;
        var paths = Enumerable.Range(0, count)
            .Select(i => $@"C:\repos\r{count - i:D4}") // descending: worst case for a per-row scan
            .ToArray();

        var viewModel = NewViewModel();
        var positionalInserts = new PositionalInsertRecorder(viewModel.Repositories);
        var report = new GitWizardReport();
        foreach (var path in paths)
            report.Repositories[path] = Repo(path);

        viewModel.PrePopulateFromReport(new GitWizardConfiguration(), paths, report);

        // Sanity: the batch is still actually sorted.
        var actual = viewModel.Repositories.Select(node => node.WorkingDirectory).ToArray();
        Assert.That(actual, Is.EqualTo(paths.OrderBy(p => p, PlatformComparer)));

        // A positional insert shifts the ObservableCollection's own elements: a
        // per-row insert into a growing, reverse-sorted list would land mid-list almost every
        // time, while the bulk path only ever appends at the tail.
        Assert.That(positionalInserts.Count, Is.Zero,
            "PrePopulateFromReport must write the batch back with tail-only appends, not positional inserts.");
    }

    // The bulk path above is for whole-batch callers; a single Live/refresh arrival must still use
    // InsertRepositoryNode's per-row binary search rather than a linear scan.
    [Test]
    public void WorkingDirectory_IsolatedArrival_LandsAtSortedPositionWithOnePositionalInsert()
    {
        const int existingCount = 200;
        var viewModel = NewViewModel();
        var positionalInserts = new PositionalInsertRecorder(viewModel.Repositories);
        var paths = Enumerable.Range(0, existingCount).Select(i => $@"C:\repos\r{i:D4}").ToArray();
        var report = new GitWizardReport();
        foreach (var path in paths)
            report.Repositories[path] = Repo(path);
        viewModel.PrePopulateFromReport(new GitWizardConfiguration(), paths, report);

        // Sorts strictly between "C:\repos\r0099" and "C:\repos\r0100" - a genuine mid-list
        // insert, not an append at either end.
        viewModel.AddRepository(Repo(@"C:\repos\r0099a"));

        Assert.That(viewModel.Repositories.Select(node => node.WorkingDirectory).ToArray(),
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

        var reads = CountingList.Install(viewModel.Repositories);

        // Sorts strictly between "C:\repos\r0499" and "C:\repos\r0500": a genuine mid-list insert.
        viewModel.AddRepository(Repo(@"C:\repos\r0499a"));
        var searchReads = reads.IndexerReads;

        Assert.That(viewModel.Repositories.Select(node => node.WorkingDirectory).ToArray(),
            Is.EqualTo(paths.Append(@"C:\repos\r0499a").OrderBy(p => p, PlatformComparer)));

        // Binary search over 1000 rows needs at most ceil(log2(1001)) = 10 reads; a linear scan
        // needs about 500. 2 * 10 leaves headroom for incidental reads while ruling out a scan.
        Assert.That(searchReads, Is.GreaterThan(0), "The counting list must observe the search.");
        Assert.That(searchReads, Is.LessThanOrEqualTo(2 * 10),
            "A single isolated arrival must use a binary search, not a linear scan.");
    }

    // pr-crew review of PR #241/#243 (round 2): the binary search above still leaves the
    // ObservableCollection's own O(n) shift per InsertRepositoryNode call, so N drained arrivals
    // through AddRepositoriesBatch (as MainViewModel.Commands.cs's UI-command-queue drain now
    // calls, instead of one AddRepository call per command) must merge the batch once instead of
    // inserting each row.
    // Round-5 pr-crew review of PR #241/#243: during a refresh, each drained batch appends in
    // arrival order (O(1) per row with zero positional inserts); the entire collection is sorted
    // once when the refresh completes (CompleteRefresh / ApplyFilterAndGrouping).
    [Test]
    public void WorkingDirectory_DrainedRefreshBatch_MergesOnceInsteadOfPerRowInsert()
    {
        const int count = 200;
        var repositories = Enumerable.Range(0, count)
            .Select(i => Repo($@"C:\repos\r{count - i:D4}")) // descending: worst case for a per-row insert
            .ToList();

        var viewModel = NewViewModel();
        var positionalInserts = new PositionalInsertRecorder(viewModel.Repositories);
        viewModel.IsRefreshing = true;
        viewModel.AddRepositoriesBatch(repositories);

        Assert.That(positionalInserts.Count, Is.Zero,
            "A drained batch must append into the display, not call InsertRepositoryNode per row.");

        // A second, smaller batch that arrives later must also append with zero positional inserts.
        var secondBatch = Enumerable.Range(0, 20).Select(i => Repo($@"C:\repos\m{i:D4}")).ToList();
        viewModel.AddRepositoriesBatch(secondBatch);

        Assert.That(positionalInserts.Count, Is.Zero,
            "Merging a second batch during refresh must still use arrival-order append without positional inserts.");

        viewModel.CompleteRefresh();

        var afterComplete = viewModel.Repositories.Select(node => node.WorkingDirectory).ToArray();
        Assert.That(afterComplete, Is.EqualTo(
            repositories.Concat(secondBatch).Select(r => r.WorkingDirectory).OrderBy(p => p, PlatformComparer)),
            "After refresh completes, the entire collection is sorted once.");
    }

    // Issue #245 / round-3 pr-crew review of PR #241/#243: a refresh worker enqueues
    // RepositoryCreated, refreshes, then enqueues RefreshCompleted, so with parallel workers the
    // command queue alternates create/complete after the initial burst. The drain coalesces all
    // creates in the drained slice into one batch and appends them in arrival order, processing
    // staged completions afterwards. One sorted rebuild at completion orders the final list.
    [Test]
    public void WorkingDirectory_DrainWithInterleavedCompletions_MergesAllCreatesOnce()
    {
        const int count = 200;
        var repositories = Enumerable.Range(0, count)
            .Select(i => Repo($@"C:\repos\r{count - i:D4}")) // descending: worst case for a per-row insert
            .ToList();

        var viewModel = NewViewModel();
        var positionalInserts = new PositionalInsertRecorder(viewModel.Repositories);
        viewModel.IsRefreshing = true;
        foreach (var repository in repositories)
        {
            viewModel.OnRepositoryCreated(repository);
            viewModel.OnRepositoryRefreshCompleted(repository);
        }

        viewModel.DrainUICommands();

        Assert.That(positionalInserts.Count, Is.Zero,
            "A drain with interleaved create/complete commands must append all creates with zero positional inserts.");
        Assert.That(viewModel._repositoryMap, Has.Count.EqualTo(count),
            "Every drained create must have produced exactly one map entry.");

        viewModel.CompleteRefresh();

        var actual = viewModel.Repositories.Select(node => node.WorkingDirectory).ToArray();
        Assert.That(actual, Is.EqualTo(repositories.Select(r => r.WorkingDirectory).OrderBy(p => p, PlatformComparer)),
            "Interleaved completions must not change the final sorted order after completion.");
    }

    // Round-4 and round-5 pr-crew review of PR #241/#243: successive drains append arriving rows
    // in O(1) time without collection resets, shifts, or rewriting accumulated rows.
    [Test]
    public void WorkingDirectory_MultipleSuccessiveDrains_DoesNotResetOrRewriteAccumulatedRows()
    {
        var viewModel = NewViewModel();
        var positionalInserts = new PositionalInsertRecorder(viewModel.Repositories);
        viewModel.IsRefreshing = true;
        var resetCount = 0;
        var addCount = 0;
        viewModel.Repositories.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
                resetCount++;
            else if (args.Action == NotifyCollectionChangedAction.Add)
                addCount += args.NewItems?.Count ?? 0;
        };

        const int batches = 5;
        const int itemsPerBatch = 20;
        var allExpected = new List<string>();

        // Emit 5 successive drains with interleaved create/completions across the alphabet
        for (var b = 0; b < batches; b++)
        {
            var batchPrefix = (char)('a' + b * 2);
            var batchRepos = Enumerable.Range(0, itemsPerBatch)
                .Select(i => Repo($@"C:\repos\{batchPrefix}_{i:D3}"))
                .ToList();

            foreach (var repo in batchRepos)
            {
                viewModel.OnRepositoryCreated(repo);
                viewModel.OnRepositoryRefreshCompleted(repo);
            }

            allExpected.AddRange(batchRepos.Select(r => r.WorkingDirectory!));
            viewModel.DrainUICommands();
        }

        Assert.That(positionalInserts.Count, Is.Zero,
            "All successive drains during a refresh must append in arrival order with zero positional inserts.");
        Assert.That(resetCount, Is.Zero,
            "Successive drains must not clear the collection or trigger Reset events.");
        Assert.That(addCount, Is.EqualTo(batches * itemsPerBatch),
            "Every repository must be added exactly once across all drains without rewriting accumulated rows.");

        viewModel.CompleteRefresh();

        var actual = viewModel.Repositories.Select(node => node.WorkingDirectory).ToArray();
        Assert.That(actual, Is.EqualTo(allExpected.OrderBy(p => p, PlatformComparer)),
            "All items across multiple drains must end up in sorted order after refresh completes.");
    }

    [Test]
    public void AddRepositoriesBatch_DuplicatePathWithinOneBatch_KeepsOnlyLatestNode()
    {
        var viewModel = NewViewModel();
        var stalePath = @"C:\repos\alpha";
        var stale = Repo(stalePath);
        var fresh = Repo(stalePath);

        viewModel.AddRepositoriesBatch([stale, Repo(@"C:\repos\beta"), fresh]);

        Assert.That(viewModel.Repositories.Count(n => n.WorkingDirectory == stalePath), Is.EqualTo(1),
            "A path repeated within one drained batch must produce exactly one display row.");
        Assert.That(viewModel._repositoryMap[stalePath].Repository, Is.SameAs(fresh),
            "The later occurrence within the batch must win.");
        Assert.That(viewModel._allRepositories, Has.Count.EqualTo(2));
    }

    [Test]
    public void GroupByDrive_PrePopulateFromReport_LargeBatch_MergesGroupChildrenOnceInsteadOfPerRow()
    {
        const int count = 200;
        var paths = Enumerable.Range(0, count)
            .Select(i => $@"C:\repos\r{count - i:D4}") // descending: worst case for a per-row insert
            .ToArray();

        var viewModel = NewViewModel();
        viewModel.ToggleGroupMode(GroupMode.Drive);

        // ToggleGroupMode replaced Repositories, so observe the collection that is displayed now.
        var publication = new GroupPublicationRecorder(viewModel.Repositories);
        var report = new GitWizardReport();
        foreach (var path in paths)
            report.Repositories[path] = Repo(path);

        viewModel.PrePopulateFromReport(new GitWizardConfiguration(), paths, report);

        var header = viewModel.Repositories.Single(n => n.IsGroupHeader);
        Assert.That(header.Children.Select(n => n.WorkingDirectory),
            Is.EqualTo(paths.OrderBy(p => p, PlatformComparer)));
        Assert.That(publication.PublishedHeaderChildCounts, Is.EqualTo(new[] { count }),
            "The group header must be published once, already holding every child.");
        Assert.That(publication.ChildMutationsAfterPublication, Is.Zero,
            "Grouped pre-population must build a group's children once, not call AddToGroups per row.");
        Assert.That(publication.PositionalInserts, Is.Zero);
    }

    [Test]
    public void AddRepositoriesBatch_GroupedDrain_MergesIntoAlreadyExistingHeader()
    {
        var viewModel = NewViewModel();
        viewModel.ToggleGroupMode(GroupMode.Drive);

        // First batch creates the header fresh (PrePopulateFromReport-style, via
        // AddRepositoriesBatch itself since it shares the same MergeGroupBatch helper).
        viewModel.AddRepositoriesBatch([Repo(@"C:\repos\alpha"), Repo(@"C:\repos\zulu")]);
        var header = viewModel.Repositories.Single(n => n.IsGroupHeader);
        Assert.That(header.Children, Has.Count.EqualTo(2));

        // A second, later drain on the same drive must merge into that same header rather than
        // creating a duplicate "C:\" group.
        viewModel.AddRepositoriesBatch([Repo(@"C:\repos\middle"), Repo(@"C:\repos\beta")]);

        var headers = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();
        Assert.That(headers, Has.Count.EqualTo(1));

        viewModel.CompleteRefresh();

        Assert.That(headers[0].Children.Select(n => n.WorkingDirectory),
            Is.EqualTo(new[] { @"C:\repos\alpha", @"C:\repos\beta", @"C:\repos\middle", @"C:\repos\zulu" }
                .OrderBy(p => p, PlatformComparer)));
    }

    [Test]
    public void HeaderGroupCount_MatchesGroupsShown_EvenWhenAGroupIsExpanded()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo(@"C:\r1"));
        viewModel.AddRepository(Repo(@"D:\r1"));
        viewModel.AddRepository(Repo(@"E:\r1"));

        viewModel.ApplyGroup("GroupByDrive");
        var expectedGroupCount = viewModel.Repositories.Count(n => n.IsGroupHeader);

        // Expanding a group used to inflate Repositories.Count with the header's own children
        // (they are inserted as flat rows for display), which UpdateHeaderWithFilterInfo then
        // mistook for the group count (issue #241 addendum: "953 repositories in 2 groups" while
        // six groups were listed).
        viewModel.ToggleGroupExpand(viewModel.Repositories.First(n => n.IsGroupHeader));

        Assert.That(viewModel.HeaderText, Does.Contain($"in {expectedGroupCount} groups"));
    }

    // Round-5 pr-crew review of PR #241/#243: during a refresh, each drain appends rows in arrival
    // order with zero positional inserts. When the refresh completes, one sorted rebuild replaces
    // the collection in a single step, correctly ordering all repositories (including mixed-case
    // drive letters on Windows) while preserving selection.
    [Test]
    [TestCase(false, TestName = "WorkingDirectory_RefreshCommandQueue_ReverseOrder_ZeroPositionalInsertsAndSortsAtEnd")]
    [TestCase(true, TestName = "WorkingDirectory_RefreshCommandQueue_RandomOrder_ZeroPositionalInsertsAndSortsAtEnd")]
    public void WorkingDirectory_RefreshCommandQueue_ZeroPositionalInsertsAndSortsAtEnd(bool randomOrder)
    {
        var viewModel = NewViewModel();
        var positionalInserts = new PositionalInsertRecorder(viewModel.Repositories);
        var drainedCollection = viewModel.Repositories;
        viewModel.IsRefreshing = true;

        // Mixed drives and mixed case: C:\, c:\, D:\, d:\, E:\, e:\
        var drives = new[] { @"C:\repos\", @"c:\repos\", @"D:\repos\", @"d:\repos\", @"E:\repos\", @"e:\repos\" };
        const int count = 60;
        var repoList = new List<GitWizardRepository>();
        for (var i = 0; i < count; i++)
        {
            var drive = drives[i % drives.Length];
            repoList.Add(Repo($@"{drive}repo_{i:D3}"));
        }

        var reposToFeed = randomOrder
            ? repoList.OrderBy(_ => Random.Shared.Next()).ToList()
            : repoList.OrderByDescending(r => r.WorkingDirectory, PlatformComparer).ToList();

        var allExpected = reposToFeed.Select(r => r.WorkingDirectory!).ToArray();

        // Feed in multiple drains, interleaving creates and completions
        const int batchSize = 10;
        for (var i = 0; i < reposToFeed.Count; i += batchSize)
        {
            var chunk = reposToFeed.Skip(i).Take(batchSize);
            foreach (var repo in chunk)
            {
                viewModel.OnRepositoryCreated(repo);
                viewModel.OnRepositoryRefreshCompleted(repo);
            }
            viewModel.DrainUICommands();
            Assert.That(positionalInserts.Count, Is.Zero,
                "During a refresh, every drain must append rows in arrival order with zero positional inserts.");
        }

        // Selection preservation: select one of the nodes before completion
        var repoToSelect = viewModel.Repositories[15];
        viewModel.SelectedRepository = repoToSelect;

        var drainedMutationsDuringCompletion = 0;
        drainedCollection.CollectionChanged += (_, _) => drainedMutationsDuringCompletion++;
        var completion = new CollectionReplacementRecorder(viewModel);

        viewModel.CompleteRefresh();

        Assert.That(positionalInserts.Count, Is.Zero,
            "Rebuilding at completion must replace the collection in a single step with zero positional inserts.");
        Assert.That(completion.ReplacedCollectionRowCounts, Is.EqualTo(new[] { count }),
            "Completion must publish exactly one replacement Repositories collection, already holding every row.");
        Assert.That(completion.MutationsAfterReplacement, Is.Zero,
            "The published collection must not be mutated incrementally after it replaces the drained one.");
        Assert.That(drainedMutationsDuringCompletion, Is.Zero,
            "Completion must not mutate the drained collection in place.");
        Assert.That(viewModel.Repositories, Is.Not.SameAs(drainedCollection));

        var actual = viewModel.Repositories.Select(node => node.WorkingDirectory).ToArray();
        Assert.That(actual, Is.EqualTo(allExpected.OrderBy(p => p, PlatformComparer)),
            "After completion, the displayed collection must be correctly sorted, including mixed-case drive letters.");

        Assert.That(viewModel.SelectedRepository?.WorkingDirectory, Is.EqualTo(repoToSelect.WorkingDirectory),
            "Selection must be preserved across the end-of-refresh sorted rebuild.");
    }
}

// Counts Add notifications that land before the tail: an ObservableCollection shifts every following
// element on such an insert, while tail appends never do.
sealed class PositionalInsertRecorder
{
    public int Count { get; private set; }

    public PositionalInsertRecorder(ObservableCollection<RepositoryNodeViewModel> nodes) =>
        nodes.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add
                && args.NewStartingIndex + (args.NewItems?.Count ?? 0) != nodes.Count)
                Count++;
        };
}

// Observes a displayed collection that receives group headers: records how many children each header
// holds when it is published and every later change to a published header's Children.
sealed class GroupPublicationRecorder
{
    public List<int> PublishedHeaderChildCounts { get; } = [];
    public int ChildMutationsAfterPublication { get; private set; }
    public int PositionalInserts { get; private set; }

    public GroupPublicationRecorder(ObservableCollection<RepositoryNodeViewModel> displayed) =>
        displayed.CollectionChanged += (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Add || args.NewItems == null)
                return;
            if (args.NewStartingIndex + args.NewItems.Count != displayed.Count)
                PositionalInserts++;
            foreach (RepositoryNodeViewModel node in args.NewItems)
            {
                if (!node.IsGroupHeader)
                    continue;
                PublishedHeaderChildCounts.Add(node.Children.Count);
                node.Children.CollectionChanged += (_, _) => ChildMutationsAfterPublication++;
            }
        };
}

// Observes every replacement of MainViewModel.Repositories: the row count each replacement holds
// when published and any change made to a replacement afterwards.
sealed class CollectionReplacementRecorder
{
    public List<int> ReplacedCollectionRowCounts { get; } = [];
    public int MutationsAfterReplacement { get; private set; }

    public CollectionReplacementRecorder(MainViewModel viewModel) =>
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(MainViewModel.Repositories))
                return;
            var published = viewModel.Repositories;
            ReplacedCollectionRowCounts.Add(published.Count);
            published.CollectionChanged += (_, _) => MutationsAfterReplacement++;
        };
}

// Replaces the private backing list of an ObservableCollection (System.Collections.ObjectModel.Collection's
// "items" field) with a forwarding list that counts indexer reads, so a test can observe how many
// existing rows a search touched without any production instrumentation.
sealed class CountingList(IList<RepositoryNodeViewModel> inner) : IList<RepositoryNodeViewModel>
{
    public int IndexerReads { get; private set; }

    public static CountingList Install(ObservableCollection<RepositoryNodeViewModel> collection)
    {
        var field = typeof(Collection<RepositoryNodeViewModel>)
            .GetField("items", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, "Collection<T> no longer exposes its backing list field.");
        var counting = new CountingList((IList<RepositoryNodeViewModel>)field!.GetValue(collection)!);
        field.SetValue(collection, counting);
        return counting;
    }

    public RepositoryNodeViewModel this[int index]
    {
        get
        {
            IndexerReads++;
            return inner[index];
        }
        set => inner[index] = value;
    }

    public int Count => inner.Count;
    public bool IsReadOnly => inner.IsReadOnly;
    public void Add(RepositoryNodeViewModel item) => inner.Add(item);
    public void Clear() => inner.Clear();
    public bool Contains(RepositoryNodeViewModel item) => inner.Contains(item);
    public void CopyTo(RepositoryNodeViewModel[] array, int arrayIndex) => inner.CopyTo(array, arrayIndex);
    public IEnumerator<RepositoryNodeViewModel> GetEnumerator() => inner.GetEnumerator();
    public int IndexOf(RepositoryNodeViewModel item) => inner.IndexOf(item);
    public void Insert(int index, RepositoryNodeViewModel item) => inner.Insert(index, item);
    public bool Remove(RepositoryNodeViewModel item) => inner.Remove(item);
    public void RemoveAt(int index) => inner.RemoveAt(index);
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
