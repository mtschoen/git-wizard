using System.Collections.Specialized;
using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Refresh batch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryListViewModel.Rows.cs, RepositoryListViewModel.CachedRows.cs, RefreshCoordinator.UiDrain.cs, RepositoryListViewModel.cs, RepositoryListViewModel.Groups.cs, and RepositorySortOrder.cs.
public class MainViewModelRefreshBatchTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel NewViewModel() => TestViewModels.Create();

    static GitWizardRepository Repo(string workingDirectory) => new(workingDirectory);

    static readonly StringComparer PlatformComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;


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
        var positionalInserts = new PositionalInsertRecorder(viewModel.RepositoryList.Repositories);
        var report = new GitWizardReport();
        foreach (var path in paths)
        {
            report.Repositories[path] = Repo(path);
        }

        viewModel.RepositoryList.PrePopulateFromReport(new GitWizardConfiguration(), paths, report);

        // Sanity: the batch is still actually sorted.
        var actual = viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory).ToArray();
        Assert.That(actual, Is.EqualTo(paths.OrderBy(p => p, PlatformComparer)));

        // A positional insert shifts the ObservableCollection's own elements: a
        // per-row insert into a growing, reverse-sorted list would land mid-list almost every
        // time, while the bulk path only ever appends at the tail.
        Assert.That(positionalInserts.Count, Is.Zero,
            "PrePopulateFromReport must write the batch back with tail-only appends, not positional inserts.");
    }

    // pr-crew review of PR #241/#243 (round 2): the binary search above still leaves the
    // ObservableCollection's own O(n) shift per InsertRepositoryNode call, so N drained arrivals
    // through AddRepositoriesBatch (as RefreshCoordinator.UiDrain.cs's UI-command-queue drain now
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
        var positionalInserts = new PositionalInsertRecorder(viewModel.RepositoryList.Repositories);
        viewModel.Refresh.IsRefreshing = true;
        viewModel.RepositoryList.AddRepositoriesBatch(repositories, streaming: true);

        Assert.That(positionalInserts.Count, Is.Zero,
            "A drained batch must append into the display, not call InsertRepositoryNode per row.");

        // A second, smaller batch that arrives later must also append with zero positional inserts.
        var secondBatch = Enumerable.Range(0, 20).Select(i => Repo($@"C:\repos\m{i:D4}")).ToList();
        viewModel.RepositoryList.AddRepositoriesBatch(secondBatch, streaming: true);

        Assert.That(positionalInserts.Count, Is.Zero,
            "Merging a second batch during refresh must still use arrival-order append without positional inserts.");

        viewModel.Refresh.CompleteRefresh();

        var afterComplete = viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory).ToArray();
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
        var positionalInserts = new PositionalInsertRecorder(viewModel.RepositoryList.Repositories);
        viewModel.Refresh.IsRefreshing = true;
        foreach (var repository in repositories)
        {
            viewModel.Refresh.OnRepositoryCreated(repository);
            viewModel.Refresh.OnRepositoryRefreshCompleted(repository);
        }

        viewModel.Refresh.DrainUICommands();

        Assert.That(positionalInserts.Count, Is.Zero,
            "A drain with interleaved create/complete commands must append all creates with zero positional inserts.");
        Assert.That(viewModel.RepositoryList.RepositoryMap, Has.Count.EqualTo(count),
            "Every drained create must have produced exactly one map entry.");

        viewModel.Refresh.CompleteRefresh();

        var actual = viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory).ToArray();
        Assert.That(actual, Is.EqualTo(repositories.Select(r => r.WorkingDirectory).OrderBy(p => p, PlatformComparer)),
            "Interleaved completions must not change the final sorted order after completion.");
    }

    // Round-4 and round-5 pr-crew review of PR #241/#243: successive drains append arriving rows
    // in O(1) time without collection resets, shifts, or rewriting accumulated rows.
    [Test]
    public void WorkingDirectory_MultipleSuccessiveDrains_DoesNotResetOrRewriteAccumulatedRows()
    {
        var viewModel = NewViewModel();
        var positionalInserts = new PositionalInsertRecorder(viewModel.RepositoryList.Repositories);
        viewModel.Refresh.IsRefreshing = true;
        var resetCount = 0;
        var addCount = 0;
        viewModel.RepositoryList.Repositories.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                resetCount++;
            }
            else if (args.Action == NotifyCollectionChangedAction.Add)
            {
                addCount += args.NewItems?.Count ?? 0;
            }
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
                viewModel.Refresh.OnRepositoryCreated(repo);
                viewModel.Refresh.OnRepositoryRefreshCompleted(repo);
            }

            allExpected.AddRange(batchRepos.Select(r => r.WorkingDirectory!));
            viewModel.Refresh.DrainUICommands();
        }

        Assert.That(positionalInserts.Count, Is.Zero,
            "All successive drains during a refresh must append in arrival order with zero positional inserts.");
        Assert.That(resetCount, Is.Zero,
            "Successive drains must not clear the collection or trigger Reset events.");
        Assert.That(addCount, Is.EqualTo(batches * itemsPerBatch),
            "Every repository must be added exactly once across all drains without rewriting accumulated rows.");

        viewModel.Refresh.CompleteRefresh();

        var actual = viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory).ToArray();
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

        viewModel.RepositoryList.AddRepositoriesBatch([stale, Repo(@"C:\repos\beta"), fresh], streaming: false);

        Assert.That(viewModel.RepositoryList.Repositories.Count(n => n.WorkingDirectory == stalePath), Is.EqualTo(1),
            "A path repeated within one drained batch must produce exactly one display row.");
        Assert.That(viewModel.RepositoryList.RepositoryMap[stalePath].Repository, Is.SameAs(fresh),
            "The later occurrence within the batch must win.");
        Assert.That(viewModel.RepositoryList.AllRepositories, Has.Count.EqualTo(2));
    }

    [Test]
    public void GroupByDrive_PrePopulateFromReport_LargeBatch_MergesGroupChildrenOnceInsteadOfPerRow()
    {
        const int count = 200;
        var paths = Enumerable.Range(0, count)
            .Select(i => $@"C:\repos\r{count - i:D4}") // descending: worst case for a per-row insert
            .ToArray();

        var viewModel = NewViewModel();
        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        // ToggleGroupMode replaced Repositories, so observe the collection that is displayed now.
        var publication = new GroupPublicationRecorder(viewModel.RepositoryList.Repositories);
        var report = new GitWizardReport();
        foreach (var path in paths)
        {
            report.Repositories[path] = Repo(path);
        }

        viewModel.RepositoryList.PrePopulateFromReport(new GitWizardConfiguration(), paths, report);

        var header = viewModel.RepositoryList.Repositories.Single(n => n.IsGroupHeader);
        Assert.That(header.Children.Select(n => n.WorkingDirectory),
            Is.EqualTo(paths.OrderBy(p => p, PlatformComparer)));
        Assert.That(publication.PublishedHeaderChildCounts, Is.EqualTo([count]),
            "The group header must be published once, already holding every child.");
        Assert.That(publication.ChildMutationsAfterPublication, Is.Zero,
            "Grouped pre-population must build a group's children once, not call AddToGroups per row.");
        Assert.That(publication.PositionalInserts, Is.Zero);
    }

    [Test]
    public void AddRepositoriesBatch_GroupedDrain_MergesIntoAlreadyExistingHeader()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        // First batch creates the header fresh (PrePopulateFromReport-style, via
        // AddRepositoriesBatch itself since it shares the same MergeGroupBatch helper).
        viewModel.RepositoryList.AddRepositoriesBatch([Repo(@"C:\repos\alpha"), Repo(@"C:\repos\zulu")], streaming: false);
        var header = viewModel.RepositoryList.Repositories.Single(n => n.IsGroupHeader);
        Assert.That(header.Children, Has.Count.EqualTo(2));

        // A second, later drain on the same drive must merge into that same header rather than
        // creating a duplicate "C:\" group.
        viewModel.RepositoryList.AddRepositoriesBatch([Repo(@"C:\repos\middle"), Repo(@"C:\repos\beta")], streaming: false);

        var headers = viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader).ToList();
        Assert.That(headers, Has.Count.EqualTo(1));

        viewModel.Refresh.CompleteRefresh();

        Assert.That(headers[0].Children.Select(n => n.WorkingDirectory),
            Is.EqualTo(new[] { @"C:\repos\alpha", @"C:\repos\beta", @"C:\repos\middle", @"C:\repos\zulu" }
                .OrderBy(p => p, PlatformComparer)));
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
        var positionalInserts = new PositionalInsertRecorder(viewModel.RepositoryList.Repositories);
        var drainedCollection = viewModel.RepositoryList.Repositories;
        viewModel.Refresh.IsRefreshing = true;

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
                viewModel.Refresh.OnRepositoryCreated(repo);
                viewModel.Refresh.OnRepositoryRefreshCompleted(repo);
            }
            viewModel.Refresh.DrainUICommands();
            Assert.That(positionalInserts.Count, Is.Zero,
                "During a refresh, every drain must append rows in arrival order with zero positional inserts.");
        }

        // Selection preservation: select one of the nodes before completion
        var repoToSelect = viewModel.RepositoryList.Repositories[15];
        viewModel.RepositoryList.SelectedRepository = repoToSelect;

        var drainedMutationsDuringCompletion = 0;
        drainedCollection.CollectionChanged += (_, _) => drainedMutationsDuringCompletion++;
        var completion = new CollectionReplacementRecorder(viewModel);

        viewModel.Refresh.CompleteRefresh();

        Assert.That(positionalInserts.Count, Is.Zero,
            "Rebuilding at completion must replace the collection in a single step with zero positional inserts.");
        Assert.That(completion.ReplacedCollectionRowCounts, Is.EqualTo([count]),
            "Completion must publish exactly one replacement Repositories collection, already holding every row.");
        Assert.That(completion.MutationsAfterReplacement, Is.Zero,
            "The published collection must not be mutated incrementally after it replaces the drained one.");
        Assert.That(drainedMutationsDuringCompletion, Is.Zero,
            "Completion must not mutate the drained collection in place.");
        Assert.That(viewModel.RepositoryList.Repositories, Is.Not.SameAs(drainedCollection));

        var actual = viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory).ToArray();
        Assert.That(actual, Is.EqualTo(allExpected.OrderBy(p => p, PlatformComparer)),
            "After completion, the displayed collection must be correctly sorted, including mixed-case drive letters.");

        Assert.That(viewModel.RepositoryList.SelectedRepository?.WorkingDirectory, Is.EqualTo(repoToSelect.WorkingDirectory),
            "Selection must be preserved across the end-of-refresh sorted rebuild.");
    }
}
