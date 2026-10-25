using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Refresh tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RefreshCoordinator.cs (CompleteRefresh), RepositoryListViewModel.cs (ApplyFilterAndGrouping), and RepositoryListViewModel.Rows.cs.
public class MainViewModelCompleteRefreshTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel CreateViewModel() => TestViewModels.Create();

    static GitWizardRepository Repo(string workingDirectory) => new(workingDirectory);

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
        vm.RepositoryList.AddRepository(Repo("C:/repos/keep"), streaming: false);

        // Stand-in for MainWindow.CaptureScrollAnchor: snapshots whatever currently sits at the
        // top of the displayed collection.
        string? capturedAnchor = null;
        string? restoredAnchor = null;
        void CaptureAnchor() =>
            capturedAnchor = vm.RepositoryList.Repositories.Count > 0 ? vm.RepositoryList.Repositories[0].WorkingDirectory : null;
        vm.RepositoryList.BeforeRepositoriesSwap = CaptureAnchor;
        vm.RepositoryList.AfterRepositoriesSwap = () => restoredAnchor = capturedAnchor;

        // Refresh start: the window captures the anchor explicitly, before RefreshAsync clears
        // the list (mirrors MainWindow.RefreshButton_Click calling CaptureScrollAnchor() up front).
        CaptureAnchor();
        Assert.That(capturedAnchor, Is.EqualTo("C:/repos/keep"),
            "Pre-condition: anchor captured at refresh start.");

        // RefreshAsync clears the backing collections, then streams arrivals in arrival order
        // (unsorted) while the refresh runs. "other" arrives first here, so it sits at the top of
        // the unsorted streaming list - a different row than the pre-refresh anchor.
        vm.RepositoryList.ClearRepositories();
        vm.Refresh.IsRefreshing = true;
        vm.RepositoryList.AddRepositoriesBatch([Repo("C:/repos/other"), Repo("C:/repos/keep")], streaming: true);

        Assert.That(vm.RepositoryList.Repositories[0].WorkingDirectory, Is.EqualTo("C:/repos/other"),
            "Pre-condition: the unsorted streaming list's top differs from the pre-refresh anchor.");

        // The final sorted rebuild must restore the anchor captured at refresh start, not one
        // recaptured from the unsorted streaming list at swap time.
        vm.Refresh.CompleteRefresh();

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
        var dispatcher = new QueuedUiDispatcher(isOnUiThread: false);
        var vm = TestViewModels.Create(dispatcher: dispatcher);

        vm.RepositoryList.AddRepository(Repo("C:/repos/keep"), streaming: false);

        var beforeSwapInvocationCount = 0;
        vm.RepositoryList.BeforeRepositoriesSwap = () => beforeSwapInvocationCount++;

        // A concurrent filter/search rebuild: off the UI thread, so its swap is only queued, not
        // yet executed. Called directly (rather than through SetSearchText's debounce) so the
        // ordering is driven explicitly instead of waiting out a timer.
        vm.RepositoryList.ApplyFilterAndGrouping();

        // The refresh itself proceeds: clear, stream arrivals, then complete - queuing its own
        // (suppressed) swap behind the rebuild's already-queued one.
        vm.RepositoryList.ClearRepositories();
        vm.Refresh.IsRefreshing = true;
        vm.RepositoryList.AddRepositoriesBatch([Repo("C:/repos/other"), Repo("C:/repos/keep")], streaming: true);
        vm.Refresh.CompleteRefresh();

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
}
