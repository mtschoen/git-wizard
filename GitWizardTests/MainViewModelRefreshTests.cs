using GitWizard;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

/// <summary>
/// Tests for the refresh lifecycle in <c>MainViewModel.Refresh.cs</c>, including repository
/// creation and completion, cached-report pre-population, stale-node pruning, rename cleanup,
/// consecutive refreshes, and the <c>HardRefreshAsync</c> guard.
/// </summary>
public class MainViewModelRefreshTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel CreateViewModel() =>
        new(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

    static GitWizardRepository Repo(string workingDirectory) => new(workingDirectory);

    // ── AddRepository ──────────────────────────────────────────────────

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

    // ── UpdateCompletedRepository ──────────────────────────────────────

    [Test]
    public void UpdateCompletedRepository_CallsUpdateOnMatchingNode()
    {
        var vm = CreateViewModel();
        var repo = Repo("C:/repos/alpha");
        vm.AddRepository(repo);

        // After AddRepository, the node starts as Refreshing. Simulate completion.
        var node = vm.Repositories[0];
        Assert.That(node.Status, Is.EqualTo(RefreshStatus.Refreshing),
            "Precondition: newly added node starts as Refreshing.");

        vm.UpdateCompletedRepository(repo);

        // Update() should change status from Refreshing to Success (no error, no submodule issues).
        Assert.That(node.Status, Is.EqualTo(RefreshStatus.Success),
            "Update must transition the node to its computed status.");
    }

    [Test]
    public void UpdateCompletedRepository_UnknownPath_IsNoOp()
    {
        var vm = CreateViewModel();
        vm.AddRepository(Repo("C:/repos/alpha"));

        // This should not throw or alter the list.
        vm.UpdateCompletedRepository(Repo("C:/repos/unknown"));

        Assert.That(vm.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void UpdateCompletedRepository_EmptyPath_IsNoOp()
    {
        var vm = CreateViewModel();

        // Should not throw.
        vm.UpdateCompletedRepository(Repo(""));

        Assert.That(vm.Repositories, Is.Empty);
    }

    [Test]
    public void UpdateCompletedRepository_RemovesRepoWhenFilterNoLongerMatches()
    {
        var vm = CreateViewModel();

        // Use a real temp repo that has pending changes so it matches PendingChanges filter.
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddUntrackedFile("dirty.txt");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        // At this point repo.HasPendingChanges should be true. Add it with filter on.
        vm.ToggleFilter(FilterType.PendingChanges);
        vm.AddRepository(repo);
        Assert.That(vm.Repositories, Has.Count.EqualTo(1), "Precondition: dirty repo passes filter.");

        // Now simulate: the pending file is resolved and a refresh clears the flag.
        // Delete the untracked file and refresh again.
        File.Delete(Path.Combine(fixture.Path, "dirty.txt"));
        repo.Refresh();

        vm.UpdateCompletedRepository(repo);

        Assert.That(vm.Repositories, Is.Empty,
            "After update, a repo that no longer matches the active filter must be removed.");
    }

    [Test]
    public void UpdateCompletedRepository_AddsRepoWhenFilterNewlyMatches()
    {
        var vm = CreateViewModel();
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        // Filter is PendingChanges; the repo is clean, so it's excluded on add.
        vm.ToggleFilter(FilterType.PendingChanges);
        vm.AddRepository(repo);
        Assert.That(vm.Repositories, Is.Empty, "Precondition: clean repo fails the filter.");

        // Dirty it up, refresh, then update.
        fixture.AddUntrackedFile("dirty.txt");
        repo.Refresh();

        vm.UpdateCompletedRepository(repo);

        Assert.That(vm.Repositories, Has.Count.EqualTo(1),
            "After update, a repo that now matches the active filter must be added.");
    }

    [Test]
    public void UpdateCompletedRepository_UpdatesGroupHeaders()
    {
        var vm = CreateViewModel();
        vm.ToggleGroupMode(GroupMode.Drive);

        var repo = Repo("C:/repos/alpha");
        vm.AddRepository(repo);

        // The group header's display text includes child status counts.
        var header = vm.Repositories[0];
        Assert.That(header.IsGroupHeader, Is.True, "Precondition.");

        // Set an error on the repo, then update.
        repo.RefreshError = "Something went wrong";
        vm.UpdateCompletedRepository(repo);

        // After UpdateCompletedRepository, the header's display text should be refreshed.
        // (The child node.Update() marks it Error, and the group header shows error counts.)
        Assert.That(header.DisplayText, Does.Contain("error"),
            "Group header display text must reflect updated child error counts.");
    }

    // ── ErrorLog wiring ─────────────────────────────────────────────────

    [Test]
    public void UpdateCompletedRepository_WithRefreshError_FeedsErrorLog()
    {
        var vm = CreateViewModel();
        var repo = Repo("C:/repos/alpha");
        vm.AddRepository(repo);

        repo.RefreshError = "Something went wrong";
        vm.UpdateCompletedRepository(repo);

        Assert.Multiple(() =>
        {
            Assert.That(vm.ErrorLog.Count, Is.EqualTo(1));
            var entry = vm.ErrorLog.Entries.Single();
            Assert.That(entry.Message, Is.EqualTo("Something went wrong"));
            Assert.That(entry.RepositoryPath, Is.EqualTo("C:/repos/alpha"));
        });
    }

    [Test]
    public void UpdateCompletedRepository_WithoutRefreshError_DoesNotFeedErrorLog()
    {
        var vm = CreateViewModel();
        var repo = Repo("C:/repos/alpha");
        vm.AddRepository(repo);

        vm.UpdateCompletedRepository(repo);

        Assert.That(vm.ErrorLog.Count, Is.EqualTo(0));
    }

    // ── CompleteRefresh anchor preservation ────────────────────────────

    // pr-crew final review of PR #241/#243: BeforeRepositoriesSwap is wired to the window's
    // CaptureScrollAnchor, which the window also calls explicitly at refresh start (before
    // RefreshAsync clears Repositories), so the user's pre-refresh position survives the clear.
    // CompleteRefresh's own call to ApplyFilterAndGrouping fires that same BeforeRepositoriesSwap
    // hook again for its final sorted rebuild, which must NOT recapture from the rebuilt,
    // still-unsorted streaming list and overwrite the anchor saved at refresh start.
    [Test]
    public void CompleteRefresh_PreservesAnchorCapturedAtRefreshStart_DoesNotRecaptureFromStreamingList()
    {
        var vm = CreateViewModel();

        // Pre-refresh state: the user is anchored on "keep", alone at the top of the list.
        vm.AddRepository(Repo("C:/repos/keep"));

        // Stand-in for MainWindow.CaptureScrollAnchor: snapshots whatever currently sits at the
        // top of the displayed collection.
        string? capturedAnchor = null;
        string? restoredAnchor = null;
        void CaptureAnchor() =>
            capturedAnchor = vm.Repositories.Count > 0 ? vm.Repositories[0].WorkingDirectory : null;
        vm.BeforeRepositoriesSwap = CaptureAnchor;
        vm.AfterRepositoriesSwap = () => restoredAnchor = capturedAnchor;

        // Refresh start: the window captures the anchor explicitly, before RefreshAsync clears
        // the list (mirrors MainWindow.RefreshButton_Click calling CaptureScrollAnchor() up front).
        CaptureAnchor();
        Assert.That(capturedAnchor, Is.EqualTo("C:/repos/keep"),
            "Pre-condition: anchor captured at refresh start.");

        // RefreshAsync clears the backing collections, then streams arrivals in arrival order
        // (unsorted) while the refresh runs. "other" arrives first here, so it sits at the top of
        // the unsorted streaming list - a different row than the pre-refresh anchor.
        vm._allRepositories.Clear();
        vm._repositoryMap.Clear();
        vm.Repositories.Clear();
        vm.IsRefreshing = true;
        vm.AddRepositoriesBatch(new[] { Repo("C:/repos/other"), Repo("C:/repos/keep") });

        Assert.That(vm.Repositories[0].WorkingDirectory, Is.EqualTo("C:/repos/other"),
            "Pre-condition: the unsorted streaming list's top differs from the pre-refresh anchor.");

        // The final sorted rebuild must restore the anchor captured at refresh start, not one
        // recaptured from the unsorted streaming list at swap time.
        vm.CompleteRefresh();

        Assert.That(restoredAnchor, Is.EqualTo("C:/repos/keep"),
            "The final rebuild must restore the pre-refresh anchor, not recapture it from the streaming list.");
    }

    // pr-crew round-9 review of PR #241/#243: the earlier fix suppressed the recapture with a
    // shared `_suppressAnchorCaptureOnNextSwap` field, consumed by whichever queued swap happened
    // to run first. An unrelated filter/search rebuild's swap - posted to the UI thread ahead of
    // CompleteRefresh's own swap - would consume the flag meant for CompleteRefresh: wrongly
    // suppressing the rebuild's own capture, then leaving CompleteRefresh's swap unsuppressed to
    // recapture from the streamed list exactly as before the original fix. suppressAnchorCapture
    // is now a parameter captured by each call's own SwapInRepositories closure, so no other
    // queued swap can consume or leak it. This drives a manual dispatcher queue explicitly (no
    // sleeps) to reproduce the exact ordering: the rebuild's swap is queued, then CompleteRefresh's
    // swap is queued behind it, before either runs.
    [Test]
    public void CompleteRefresh_AnchorSuppression_SurvivesAnUnrelatedRebuildsSwapQueuedAheadOfIt()
    {
        var dispatcher = new ManualQueueUiDispatcher();
        var vm = new MainViewModel(dispatcher, new StubUserDialogs(), new StubClipboardService());

        vm.AddRepository(Repo("C:/repos/keep"));

        var beforeSwapInvocationCount = 0;
        vm.BeforeRepositoriesSwap = () => beforeSwapInvocationCount++;

        // A concurrent filter/search rebuild: off the UI thread, so its swap is only queued, not
        // yet executed. Called directly (rather than through SetSearchText's debounce) so the
        // ordering is driven explicitly instead of waiting out a timer.
        vm.ApplyFilterAndGrouping();

        // The refresh itself proceeds: clear, stream arrivals, then complete - queuing its own
        // (suppressed) swap behind the rebuild's already-queued one.
        vm._allRepositories.Clear();
        vm._repositoryMap.Clear();
        vm.Repositories.Clear();
        vm.IsRefreshing = true;
        vm.AddRepositoriesBatch(new[] { Repo("C:/repos/other"), Repo("C:/repos/keep") });
        vm.CompleteRefresh();

        Assert.That(dispatcher.QueuedCount, Is.EqualTo(2),
            "Pre-condition: both swaps are queued and neither has run yet.");

        // Drain in FIFO order: the unrelated rebuild's swap runs first.
        dispatcher.RunNext();
        Assert.That(beforeSwapInvocationCount, Is.EqualTo(1),
            "The unrelated rebuild's own swap must still capture normally - its capture must not " +
            "be stolen by CompleteRefresh's later, unrelated call.");

        // CompleteRefresh's own swap runs second and must not perform a second capture - a second
        // invocation here would mean it recaptured from the list instead of preserving the anchor
        // captured at refresh start, exactly the regression the round-9 review flagged.
        dispatcher.RunNext();
        Assert.That(beforeSwapInvocationCount, Is.EqualTo(1),
            "CompleteRefresh's own swap must remain suppressed even though an unrelated swap ran " +
            "ahead of it in the queue - the pre-refresh anchor must survive to its restore.");
    }

    // Minimal IUiDispatcher double that always reports it is off the UI thread, so every call to
    // ApplyFilterAndGrouping queues its swap via Post instead of running it inline - letting a test
    // drive exactly when each queued swap runs, in FIFO order, with no real threading or sleeps.
    sealed class ManualQueueUiDispatcher : IUiDispatcher
    {
        readonly Queue<Action> _queued = new();

        public bool IsOnUiThread => false;
        public int QueuedCount => _queued.Count;
        public void Post(Action action) => _queued.Enqueue(action);
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task InvokeAsync(Func<Task> action) => action();

        public void RunNext()
        {
            if (!_queued.TryDequeue(out var action))
                throw new InvalidOperationException("No UI callback is queued.");
            action();
        }
    }

    // ── HardRefreshAsync ──────────────────────────────────────────────

    [Test]
    public async Task HardRefreshAsync_ReturnsImmediately_WhenAlreadyRefreshing()
    {
        var vm = CreateViewModel();
        vm.IsRefreshing = true;

        // HardRefreshAsync must return without side effects.
        await vm.HardRefreshAsync();

        Assert.That(vm.IsRefreshing, Is.True,
            "HardRefreshAsync must be a no-op when already refreshing.");
    }

    [Test]
    public async Task HardRefreshAsync_UnusableDiscovery_PreservesCachedList()
    {
        var home = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();
        try
        {
            var searchRoot = Directory.CreateDirectory(Path.Combine(home, "search")).FullName;
            var cachedRepo = GitWizardApi.NormalizePath(Path.Combine(searchRoot, "cached-repo"));
            await GitWizardApi.SaveCachedRepositoryPathsAsync([cachedRepo]);
            var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync();
            configuration.SearchPaths.Add(searchRoot);

            var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
            {
                IsWindows = true,
                IsLinux = false,
                ClassifyRoot = root => new RootClassification(root, RootFileSystem.Recursive, null, null),
                CrawlRootAsync = (_, root, _) => Task.FromResult(new RecursiveDiscovery(
                    [], [root], new Dictionary<string, string> { [root] = "access denied" })),
                OpenIndexAsync = (_, _, _, _) => throw new AssertionException("Recursive roots must not open an index."),
                ReadListAsync = token => GitWizardApi.GetCachedRepositoryPathsAsync(token),
                WriteListAsync = (paths, token) => GitWizardApi.SaveCachedRepositoryPathsAsync(paths, token),
                CandidateExists = RepositoryCandidateProbe.Exists,
                CreateLinuxSource = () => throw new PlatformNotSupportedException()
            });

            var vm = CreateViewModel();
            vm.RefreshDiscoveryCoordinator = coordinator;

            await vm.HardRefreshAsync();

            var cached = await GitWizardApi.GetCachedRepositoryPathsAsync();
            Assert.Multiple(() =>
            {
                Assert.That(vm.IsRefreshing, Is.False);
                Assert.That(cached, Is.Not.Null, "An unusable rebuild must preserve the cached list.");
                Assert.That(cached, Does.Contain(cachedRepo).IgnoreCase);
                Assert.That(vm.HeaderText, Does.Contain("discovery incomplete").IgnoreCase,
                    "The incomplete outcome must be visible, not silently swallowed.");
            });
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(home);
            TestUtilities.ResetStaticCaches();
        }
    }

    // ── RefreshAsync ──────────────────────────────────────────────────

    [Test]
    public async Task RefreshAsync_ReturnsImmediately_WhenAlreadyRefreshing()
    {
        var vm = CreateViewModel();
        vm.IsRefreshing = true;

        // Should return immediately, not start a second refresh.
        await vm.RefreshAsync();

        Assert.That(vm.IsRefreshing, Is.True,
            "RefreshAsync must be a no-op when already refreshing.");
    }

    // ── RemoveRepositoryPathFromUi (the internal entry point Live mode uses to drop one repo) ──

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

    // ── ProcessUICommand dispatch (via IUpdateHandler) ────────────────

    [Test]
    public void OnRepositoryCreated_QueuesAndProcessesViaAddRepository()
    {
        var vm = CreateViewModel();
        var repo = Repo("C:/repos/alpha");

        // OnRepositoryCreated enqueues a RepositoryCreated command.
        vm.OnRepositoryCreated(repo);

        // The batching window drains the queue asynchronously; verify the command was queued by
        // checking it didn't add inline. (AddRepository is called by the scheduled drain, not inline.)
        // This is an integration-boundary test: the IUpdateHandler interface is correctly wired.
        Assert.That(vm.Repositories, Is.Empty,
            "OnRepositoryCreated queues the repo asynchronously; it should not add inline.");
    }

    [Test]
    public void OnRepositoryRefreshCompleted_QueuesRefreshCompletedCommand()
    {
        var vm = CreateViewModel();
        var repo = Repo("C:/repos/alpha");

        // First add the repo directly so the map is populated.
        vm.AddRepository(repo);
        Assert.That(vm.Repositories, Has.Count.EqualTo(1), "Precondition.");

        // Queue a refresh completed event.
        vm.OnRepositoryRefreshCompleted(repo);

        // The command is queued, not processed inline.
        Assert.That(vm.Repositories[0].Status, Is.EqualTo(RefreshStatus.Refreshing),
            "OnRepositoryRefreshCompleted queues the command; it does not update inline.");
    }

    // ── AddSubmodule (tested indirectly) ─────────────────────────────

    [Test]
    public void OnSubmoduleCreated_QueuesSubmoduleCommand()
    {
        var vm = CreateViewModel();
        var parent = Repo("C:/repos/parent");
        var sub = Repo("C:/repos/parent/sub");

        vm.AddRepository(parent);
        vm.OnSubmoduleCreated(parent, sub);

        // Submodule is queued, not processed inline.
        var parentNode = vm.Repositories[0];
        Assert.That(parentNode.Children, Is.Empty,
            "OnSubmoduleCreated queues the submodule asynchronously.");
    }

    [Test]
    public void OnWorktreeCreated_QueuesWorktreeCommand()
    {
        var vm = CreateViewModel();
        var worktree = Repo("C:/repos/worktree");

        vm.OnWorktreeCreated(worktree);

        Assert.That(vm.Repositories, Is.Empty,
            "OnWorktreeCreated queues the worktree asynchronously.");
    }

    // ── Multiple repos with filter and search combined ────────────────

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

    // ── UpdateCachedPathsAfterScan ────────────────────────────────────

    [Test]
    public async Task UpdateCachedPathsAfterScan_RemovesNonRepositoryPaths()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            var validPath = "/valid/repo";
            var stalePath = "/stale/not-a-repo";

            // Write a repositories.txt with both valid and stale paths.
            await GitWizardApi.SaveCachedRepositoryPathsAsync([validPath, stalePath]);

            // Call UpdateCachedPathsAfterScan with the stale path in NonRepositoryPaths.
            await MainViewModel.UpdateCachedPathsAfterScanAsync(
                deletedPaths: [],
                renamedOldPaths: [],
                nonRepositoryPaths: new HashSet<string> { stalePath });

            // The stale path should be removed from the cache file.
            var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync();
            Assert.That(cachedPaths, Is.Not.Null);
            Assert.That(cachedPaths, Does.Not.Contain(stalePath));
            Assert.That(cachedPaths, Does.Contain(validPath));
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public async Task UpdateCachedPathsAfterScan_RemovesDeletedPaths()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            var validPath = "/valid/repo";
            var deletedPath = "/deleted/repo";

            await GitWizardApi.SaveCachedRepositoryPathsAsync([validPath, deletedPath]);

            await MainViewModel.UpdateCachedPathsAfterScanAsync(
                deletedPaths: new HashSet<string> { deletedPath },
                renamedOldPaths: [],
                nonRepositoryPaths: []);

            var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync();
            Assert.That(cachedPaths, Is.Not.Null);
            Assert.That(cachedPaths, Does.Not.Contain(deletedPath));
            Assert.That(cachedPaths, Does.Contain(validPath));
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public async Task UpdateCachedPathsAfterScan_RemovesAllThreeTypes()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            var healthyPath = "/healthy/repo";
            var deletedPath = "/deleted/repo";
            var stalePath = "/stale/not-a-repo";
            var renamedPath = "/old-name/repo";

            await GitWizardApi.SaveCachedRepositoryPathsAsync([healthyPath, deletedPath, stalePath, renamedPath]);

            await MainViewModel.UpdateCachedPathsAfterScanAsync(
                deletedPaths: new HashSet<string> { deletedPath },
                renamedOldPaths: new HashSet<string> { renamedPath },
                nonRepositoryPaths: new HashSet<string> { stalePath });

            var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync();
            Assert.That(cachedPaths, Is.Not.Null);
            Assert.That(cachedPaths!.Length, Is.EqualTo(1));
            Assert.That(cachedPaths[0], Is.EqualTo(healthyPath));
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public async Task UpdateCachedPathsAfterScan_NoPathsToRemove_NoChange()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            var path = "/valid/repo";
            await GitWizardApi.SaveCachedRepositoryPathsAsync([path]);

            await MainViewModel.UpdateCachedPathsAfterScanAsync([], [], []);

            var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync();
            Assert.That(cachedPaths, Is.Not.Null);
            Assert.That(cachedPaths!.Length, Is.EqualTo(1));
            Assert.That(cachedPaths[0], Is.EqualTo(path));
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public async Task UpdateCachedPathsAfterScan_NoCachedFile_IsNoOp()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            // No repositories.txt exists; the method should not throw.
            await MainViewModel.UpdateCachedPathsAfterScanAsync(
                deletedPaths: ["/some/deleted"],
                renamedOldPaths: [],
                nonRepositoryPaths: []);
            // If we got here without an exception, the test passes.
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    // ── Pre-population from cached report (issue #98) ──────────────────

    [Test]
    public void PrePopulateFromReport_AddsCachedReposToUi_WhenNoFilterOrGroup()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            // Create a cached report with two repos.
            var report = new GitWizardReport();
            report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
            report.Repositories["/repos/beta"] = new GitWizardRepository("/repos/beta");
            report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

            // Also write the cached repo paths.
            GitWizardApi.SaveCachedRepositoryPaths(["/repos/alpha", "/repos/beta"]);

            var vm = CreateViewModel();

            // Pre-populate from the cached report.
            var config = new GitWizardConfiguration { SearchPaths = new SortedSet<string> { "/repos" } };
            vm.PrePopulateFromReport(config, ["/repos/alpha", "/repos/beta"], report);

            // Both repos should appear in the UI immediately.
            Assert.That(vm.Repositories, Has.Count.EqualTo(2),
                "Pre-population should add cached repos to Repositories.");
            Assert.That(vm._allRepositories.Count, Is.EqualTo(2),
                "Pre-population should add cached repos to _allRepositories.");
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public void PrePopulateFromReport_SkipsReposNotInPathList()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            var report = new GitWizardReport();
            report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
            report.Repositories["/repos/beta"] = new GitWizardRepository("/repos/beta");
            report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

            // Only path "/repos/alpha" is in the repositoryPaths list.
            var vm = CreateViewModel();
            var config = new GitWizardConfiguration { SearchPaths = new SortedSet<string> { "/repos" } };
            vm.PrePopulateFromReport(config, ["/repos/alpha"], report);

            Assert.That(vm.Repositories, Has.Count.EqualTo(1),
                "Only repos in the path list should be pre-populated.");
            Assert.That(vm.Repositories[0].WorkingDirectory, Is.EqualTo("/repos/alpha"));
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public void AddRepository_SkipsDuplicateRepositoriesAdd_ForPrePopulatedRepo()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            var report = new GitWizardReport();
            report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
            report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

            var vm = CreateViewModel();
            var config = new GitWizardConfiguration { SearchPaths = new SortedSet<string> { "/repos" } };
            vm.PrePopulateFromReport(config, ["/repos/alpha"], report);

            Assert.That(vm.Repositories, Has.Count.EqualTo(1), "Pre-condition: one repo pre-populated.");

            // Simulate OnRepositoryCreated firing during the refresh - same path.
            var freshRepo = new GitWizardRepository("/repos/alpha");
            vm.AddRepository(freshRepo);

            // Should NOT have added a duplicate to Repositories.
            Assert.That(vm.Repositories, Has.Count.EqualTo(1),
                "AddRepository must not duplicate repos that were pre-populated.");

            // The node in _allRepositories (which ApplyFilterAndGrouping uses to rebuild
            // Repositories) must be the fresh scan node, not the stale cached node.
            // If it were stale the user would see old branch / status data after the
            // grouping pass rebuilds the visible collection.
            var freshNode = vm._repositoryMap["/repos/alpha"];
            Assert.That(vm._allRepositories[0], Is.SameAs(freshNode),
                "_allRepositories must hold the fresh scan node so ApplyFilterAndGrouping " +
                "rebuilds Repositories with updated data.");
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public void PrePopulateFromReport_NoOp_WhenNoCachedReport()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            // No cached report exists.
            var vm = CreateViewModel();
            var config = new GitWizardConfiguration { SearchPaths = new SortedSet<string> { "/repos" } };
            vm.PrePopulateFromReport(config, ["/repos/alpha"], null);

            Assert.That(vm.Repositories, Is.Empty,
                "Pre-population should be a no-op when no cached report exists.");
            Assert.That(vm._allRepositories, Is.Empty);
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public void PrePopulateFromReport_NoOp_WhenRepositoryPathsIsNull()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            var vm = CreateViewModel();
            var config = new GitWizardConfiguration { SearchPaths = new SortedSet<string> { "/repos" } };
            vm.PrePopulateFromReport(config, null!, null);

            Assert.That(vm.Repositories, Is.Empty,
                "Pre-population should be a no-op when repositoryPaths is null.");
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public void AddRepository_FreshScanNodeReplacesStaleCachedNode_InAllCollections()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            var report = new GitWizardReport();
            report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
            report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

            var vm = CreateViewModel();
            var config = new GitWizardConfiguration { SearchPaths = new SortedSet<string> { "/repos" } };
            vm.PrePopulateFromReport(config, ["/repos/alpha"], report);

            var cachedNode = vm._allRepositories[0];
            Assert.That(vm._repositoryMap, Does.ContainKey("/repos/alpha"),
                "Pre-condition: repo is pre-populated.");

            // Simulate a fresh scan result for the same path.
            var freshRepo = new GitWizardRepository("/repos/alpha");
            vm.AddRepository(freshRepo);

            // _allRepositories must contain the fresh node (not the cached one).
            Assert.That(vm._allRepositories, Has.Count.EqualTo(1),
                "_allRepositories must not grow on refresh.");
            Assert.That(vm._allRepositories[0], Is.Not.SameAs(cachedNode),
                "_allRepositories must hold the fresh scan node, not the stale cached node.");
            Assert.That(vm._allRepositories[0].Repository, Is.SameAs(freshRepo),
                "_allRepositories[0].Repository must be the fresh GitWizardRepository.");

            // Every collection must point to the fresh node immediately, before the
            // end-of-refresh ApplyFilterAndGrouping rebuild.
            var freshNode = vm._repositoryMap["/repos/alpha"];
            Assert.That(freshNode.Repository, Is.SameAs(freshRepo),
                "_repositoryMap must hold the fresh scan node.");
            Assert.That(vm.Repositories[0], Is.SameAs(freshNode),
                "The visible row must be replaced as soon as the fresh scan node arrives.");

            vm.UpdateCompletedRepository(freshRepo);

            Assert.That(vm.Repositories[0].Status, Is.EqualTo(RefreshStatus.Success),
                "Completion updates must reach the visible fresh row.");
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public void AddRepository_FreshScanNodeReplacesCachedNode_InGroupedView()
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

            var groupHeader = viewModel.Repositories.Single(candidate => candidate.IsGroupHeader);
            var cachedNode = groupHeader.Children.Single();
            var freshRepository = new GitWizardRepository("C:/repos/alpha");

            viewModel.AddRepository(freshRepository);

            var freshNode = viewModel._repositoryMap["C:/repos/alpha"];
            Assert.That(groupHeader.Children, Has.Count.EqualTo(1));
            Assert.That(groupHeader.Children[0], Is.SameAs(freshNode));
            Assert.That(groupHeader.Children[0], Is.Not.SameAs(cachedNode));
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
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
    public void PrePopulateFromReport_GroupedViewHonorsActiveFilter()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            var report = new GitWizardReport();
            report.Repositories["C:/repos/alpha"] = new GitWizardRepository("C:/repos/alpha");
            report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

            var viewModel = CreateViewModel();
            viewModel.ToggleFilter(FilterType.PendingChanges);
            viewModel.ToggleGroupMode(GroupMode.Drive);
            var configuration = new GitWizardConfiguration
            {
                SearchPaths = new SortedSet<string> { "C:/repos" }
            };

            viewModel.PrePopulateFromReport(configuration, ["C:/repos/alpha"], report);

            Assert.That(viewModel.Repositories, Is.Empty,
                "Grouped pre-population must not expose repositories excluded by the active filter.");
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public void PrePopulateFromReport_EmptyNextCycle_DoesNotSuppressRediscoveredRepository()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            var report = new GitWizardReport();
            report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
            report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));

            var viewModel = CreateViewModel();
            var configuration = new GitWizardConfiguration
            {
                SearchPaths = new SortedSet<string> { "/repos" }
            };
            viewModel.PrePopulateFromReport(configuration, ["/repos/alpha"], report);
            Assert.That(viewModel._repositoryMap, Does.ContainKey("/repos/alpha"));

            viewModel.Repositories.Clear();
            viewModel._allRepositories.Clear();
            viewModel._repositoryMap.Clear();
            viewModel.PrePopulateFromReport(configuration, [], report);
            viewModel.AddRepository(new GitWizardRepository("/repos/alpha"));

            Assert.That(viewModel.Repositories, Has.Count.EqualTo(1),
                "A path cached in an earlier cycle must not suppress a fresh row.");
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempHome);
        }
    }

    [Test]
    public void TwoConsecutiveRefreshes_DoesNotDuplicateRows()
    {
        var tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        try
        {
            var report = new GitWizardReport();
            report.Repositories["/repos/alpha"] = new GitWizardRepository("/repos/alpha");
            report.Repositories["/repos/beta"] = new GitWizardRepository("/repos/beta");
            report.Save(Path.Combine(GitWizardApi.GetLocalFilesPath(), "report.json"));
            GitWizardApi.SaveCachedRepositoryPaths(["/repos/alpha", "/repos/beta"]);

            var vm = CreateViewModel();
            var config = new GitWizardConfiguration { SearchPaths = new SortedSet<string> { "/repos" } };

            // First "refresh" cycle.
            vm.PrePopulateFromReport(config, ["/repos/alpha", "/repos/beta"], report);
            var freshAlpha1 = new GitWizardRepository("/repos/alpha");
            var freshBeta1 = new GitWizardRepository("/repos/beta");
            vm.AddRepository(freshAlpha1);
            vm.AddRepository(freshBeta1);

            Assert.That(vm._allRepositories, Has.Count.EqualTo(2),
                "Pre-condition: two repos after first refresh cycle.");

            // Second "refresh" cycle (simulates consecutive refresh).
            // RefreshAsync clears _allRepositories / _repositoryMap before pre-populating
            // so only nodes from the fresh scan survive.
            vm.Repositories.Clear();
            vm._allRepositories.Clear();
            vm._repositoryMap.Clear();
            vm.PrePopulateFromReport(config, ["/repos/alpha", "/repos/beta"], report);
            var freshAlpha2 = new GitWizardRepository("/repos/alpha");
            var freshBeta2 = new GitWizardRepository("/repos/beta");
            vm.AddRepository(freshAlpha2);
            vm.AddRepository(freshBeta2);

            Assert.That(vm._allRepositories, Has.Count.EqualTo(2),
                "Two consecutive refreshes must not duplicate rows in _allRepositories.");
            Assert.That(vm.Repositories, Has.Count.EqualTo(2),
                "Two consecutive refreshes must not duplicate rows in the visible collection.");
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
