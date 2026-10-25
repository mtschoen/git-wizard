using System.Runtime.CompilerServices;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Grouping interaction tests exercise filter toggling, mode selection, search debounce, and thread-swapped grouping.
// Covers RepositoryListViewModel.cs and SearchDebouncer.cs.
public class MainViewModelGroupingInteractionTests
{
    // Regression: the Repositories collection swap invokes view hooks that touch the visual tree
    // (the Avalonia ScrollViewer.Offset getter enforces UI-thread affinity). RefreshAsync reaches
    // ApplyFilterAndGrouping on a thread-pool thread via its ConfigureAwait(false) continuation, so
    // the swap must be marshaled onto the UI thread or the app crashes with "Call from invalid
    // thread". The StubUiDispatcher cannot catch this (it claims every thread is the UI thread), so
    // this test uses a dispatcher with a real, dedicated UI thread.
    [Test]
    public async Task ApplyFilterAndGrouping_ReachedOffUiThread_RunsSwapOnUiThread()
    {
        using var dispatcher = new ThreadedUiDispatcher();
        var vm = TestViewModels.Create(dispatcher: dispatcher);

        // Capture the UI thread's id as a value, not the disposable dispatcher, so the swap callback
        // can assert it ran on that thread without capturing a variable the using scope disposes.
        var uiThreadId = dispatcher.UiThreadId;
        bool? swapRanOnUiThread = null;
        vm.RepositoryList.AfterRepositoriesSwap = () => swapRanOnUiThread = Environment.CurrentManagedThreadId == uiThreadId;

        // Trigger the swap from a thread-pool thread, mirroring RefreshAsync's continuation.
        await Task.Run(() => vm.RepositoryList.SetSearchText("anything"));

        // The pump is a single-threaded FIFO queue, so this no-op runs strictly after the queued
        // swap; awaiting it guarantees the swap has executed before we assert.
        await dispatcher.InvokeAsync(() => { });

        Assert.That(swapRanOnUiThread, Is.True,
            "Repositories swap must run on the UI thread even when reached from a background thread.");
    }

    // The search box binds two-way to SearchText, whose setter fires per keystroke. Filtering is a
    // full off-screen rebuild + collection swap (costly with 700+ repos), so the setter debounces:
    // rapid keystrokes must collapse into a single deferred filter pass, not one pass per character.
    [Test]
    public async Task SearchText_DebouncesRapidKeystrokesIntoSingleFilterPass()
    {
        using var dispatcher = new ThreadedUiDispatcher();
        var vm = TestViewModels.Create(dispatcher: dispatcher);

        // Hold the count in a StrongBox so the captured variable (the box) is never reassigned -
        // only its contents mutate - which sidesteps AccessToModifiedClosure while the callback
        // still increments a single shared counter.
        var swapCount = new StrongBox<int>(0);
        var swapCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.RepositoryList.AfterRepositoriesSwap = () =>
        {
            Interlocked.Increment(ref swapCount.Value);
            swapCompleted.TrySetResult();
        };

        // Three distinct values, each fires the setter - but they arrive within the debounce window.
        vm.RepositoryList.SearchText = "a";
        vm.RepositoryList.SearchText = "ab";
        vm.RepositoryList.SearchText = "abc";

        // Deferred, not immediate: the synchronous sets must not have run a filter pass yet.
        Assert.That(Volatile.Read(ref swapCount.Value), Is.Zero,
            "Filtering must be deferred during the debounce window, not run per keystroke.");

        // The callback runs after the deferred collection swap, so its completion is the condition
        // this test needs. The timeout only bounds a broken test instead of controlling progress.
        await swapCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(swapCount.Value, Is.EqualTo(1),
                "Rapid keystrokes must coalesce into a single filter/grouping pass.");
            Assert.That(vm.RepositoryList.SearchText, Is.EqualTo("abc"));
        }
    }

    // The sidebar buttons carry their FilterType/GroupMode in Tag and the code-behind routes a
    // click to ToggleFilter/ToggleGroupMode: re-clicking the active button clears it. The
    // sidebar's `.active` highlight binds to ActiveFilter/ActiveGroupMode, so this also pins
    // that those notifying properties (not just the private fields) move. The Tag-to-action
    // mapping itself is covered by the MainWindow sidebar tests.
    [Test]
    public void ToggleFilter_TogglesActiveFilterOnRepeatClick()
    {
        var vm = TestViewModels.Create();

        vm.RepositoryList.ToggleFilter(FilterType.Stale);
        Assert.That(vm.RepositoryList.ActiveFilter, Is.EqualTo(FilterType.Stale));

        vm.RepositoryList.ToggleFilter(FilterType.Stale);
        Assert.That(vm.RepositoryList.ActiveFilter, Is.EqualTo(FilterType.None), "Re-clicking the active filter must clear it.");

        vm.RepositoryList.ToggleFilter(FilterType.PendingChanges);
        Assert.That(vm.RepositoryList.ActiveFilter, Is.EqualTo(FilterType.PendingChanges), "Clicking a different filter must switch to it.");
    }

    [Test]
    public void ToggleFilter_RaisesPropertyChangedForActiveFilter()
    {
        var vm = TestViewModels.Create();

        var raised = false;
        vm.RepositoryList.PropertyChanged += (sender, arguments) =>
        {
            Assert.That(sender, Is.SameAs(vm.RepositoryList));
            raised |= arguments.PropertyName == nameof(RepositoryListViewModel.ActiveFilter);
        };
        vm.RepositoryList.ToggleFilter(FilterType.Stale);
        Assert.That(raised, Is.True, "The active filter binding must receive the component notification.");
    }

    [Test]
    public void ToggleGroupMode_TogglesActiveGroupModeOnRepeatClick()
    {
        var vm = TestViewModels.Create();

        vm.RepositoryList.ToggleGroupMode(GroupMode.Drive);
        Assert.That(vm.RepositoryList.ActiveGroupMode, Is.EqualTo(GroupMode.Drive));

        vm.RepositoryList.ToggleGroupMode(GroupMode.Drive);
        Assert.That(vm.RepositoryList.ActiveGroupMode, Is.EqualTo(GroupMode.None), "Re-clicking the active group must clear it.");
    }

    // Sort, unlike Filter/Group, has no "off" state - one mode is always active - so a repeat click
    // must keep it selected.
    [Test]
    public void SetSortMode_SetsModeAndDoesNotToggleOff()
    {
        var vm = TestViewModels.Create();

        vm.RepositoryList.SetSortMode(SortMode.RecentlyUsed);
        Assert.That(vm.RepositoryList.ActiveSortMode, Is.EqualTo(SortMode.RecentlyUsed));

        vm.RepositoryList.SetSortMode(SortMode.RecentlyUsed);
        Assert.That(vm.RepositoryList.ActiveSortMode, Is.EqualTo(SortMode.RecentlyUsed), "Re-clicking the active sort must keep it selected.");
    }
}
