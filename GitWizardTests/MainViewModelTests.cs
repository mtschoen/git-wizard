using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GitWizard;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

public class MainViewModelTests
{
    [Test]
    public void Construction_RequiresInjectedServices()
    {
        var dispatcher = new StubUiDispatcher();
        var dialogs = new StubUserDialogs();
        var clipboard = new StubClipboardService();

        var vm = new MainViewModel(dispatcher, dialogs, clipboard);

        Assert.That(vm, Is.Not.Null);
        Assert.That(vm.HeaderText, Is.EqualTo("GitWizard"));
    }

    // Regression: the Repositories collection swap invokes view hooks that touch the visual tree
    // (the Avalonia ScrollViewer.Offset getter enforces UI-thread affinity). RefreshAsync reaches
    // ApplyFilterAndGrouping on a thread-pool thread via its ConfigureAwait(false) continuation, so
    // the swap must be marshaled onto the UI thread or the app crashes with "Call from invalid
    // thread". The StubUiDispatcher cannot catch this (it claims every thread is the UI thread), so
    // this test uses a dispatcher with a real, dedicated UI thread.
    [Test]
    public async Task ApplyFilterAndGrouping_ReachedOffUiThread_RunsSwapOnUiThread()
    {
        using var dispatcher = new PumpUiDispatcher();
        var vm = new MainViewModel(dispatcher, new StubUserDialogs(), new StubClipboardService());

        // Capture the UI thread's id as a value, not the disposable dispatcher, so the swap callback
        // can assert it ran on that thread without capturing a variable the using scope disposes.
        var uiThreadId = dispatcher.UiThreadId;
        bool? swapRanOnUiThread = null;
        vm.AfterRepositoriesSwap = () => swapRanOnUiThread = Environment.CurrentManagedThreadId == uiThreadId;

        // Trigger the swap from a thread-pool thread, mirroring RefreshAsync's continuation.
        await Task.Run(() => vm.SetSearchText("anything"));

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
        using var dispatcher = new PumpUiDispatcher();
        var vm = new MainViewModel(dispatcher, new StubUserDialogs(), new StubClipboardService());

        // Hold the count in a StrongBox so the captured variable (the box) is never reassigned -
        // only its contents mutate - which sidesteps AccessToModifiedClosure while the callback
        // still increments a single shared counter.
        var swapCount = new StrongBox<int>(0);
        vm.AfterRepositoriesSwap = () => Interlocked.Increment(ref swapCount.Value);

        // Three distinct values, each fires the setter - but they arrive within the debounce window.
        vm.SearchText = "a";
        vm.SearchText = "ab";
        vm.SearchText = "abc";

        // Deferred, not immediate: the synchronous sets must not have run a filter pass yet.
        Assert.That(Volatile.Read(ref swapCount.Value), Is.EqualTo(0),
            "Filtering must be deferred during the debounce window, not run per keystroke.");

        // Wait past the 200ms debounce window, then drain the pump so the swap has executed.
        await Task.Delay(400);
        await dispatcher.InvokeAsync(() => { });

        Assert.Multiple(() =>
        {
            Assert.That(swapCount.Value, Is.EqualTo(1),
                "Rapid keystrokes must coalesce into a single filter/grouping pass.");
            Assert.That(vm.SearchText, Is.EqualTo("abc"));
        });
    }

    // The Avalonia sidebar dispatches Filter/Group clicks by button name through ApplyFilter/
    // ApplyGroup, which must mirror the MAUI UI's toggle: re-clicking the active button clears it.
    // The sidebar's `.active` highlight binds to ActiveFilter/ActiveGroupMode, so this also pins
    // that those notifying properties (not just the private fields) move.
    [Test]
    public void ApplyFilter_TogglesActiveFilterOnRepeatClick()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

        vm.ApplyFilter("FilterStale");
        Assert.That(vm.ActiveFilter, Is.EqualTo(FilterType.Stale));

        vm.ApplyFilter("FilterStale");
        Assert.That(vm.ActiveFilter, Is.EqualTo(FilterType.None), "Re-clicking the active filter must clear it.");

        vm.ApplyFilter("FilterPendingChanges");
        Assert.That(vm.ActiveFilter, Is.EqualTo(FilterType.PendingChanges), "Clicking a different filter must switch to it.");
    }

    [Test]
    public void ApplyGroup_TogglesActiveGroupModeOnRepeatClick()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

        vm.ApplyGroup("GroupByDrive");
        Assert.That(vm.ActiveGroupMode, Is.EqualTo(GroupMode.Drive));

        vm.ApplyGroup("GroupByDrive");
        Assert.That(vm.ActiveGroupMode, Is.EqualTo(GroupMode.None), "Re-clicking the active group must clear it.");
    }

    // Sort, unlike Filter/Group, has no "off" state - one mode is always active - so a repeat click
    // must keep it selected (matching the MAUI UI, which never cleared the sort highlight).
    [Test]
    public void ApplySort_SetsModeAndDoesNotToggleOff()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

        vm.ApplySort("SortByRecentlyUsed");
        Assert.That(vm.ActiveSortMode, Is.EqualTo(SortMode.RecentlyUsed));

        vm.ApplySort("SortByRecentlyUsed");
        Assert.That(vm.ActiveSortMode, Is.EqualTo(SortMode.RecentlyUsed), "Re-clicking the active sort must keep it selected.");
    }

    [Test]
    public void ApplyFilter_RaisesPropertyChangedForActiveFilter()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

        var raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(MainViewModel.ActiveFilter);

        vm.ApplyFilter("FilterStale");

        Assert.That(raised, Is.True, "The `.active` highlight binds to ActiveFilter, so its change must notify.");
    }

    [Test]
    public void IsScanning_IsFalseBeforeAnyRefresh()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

        Assert.That(vm.IsScanning, Is.False);
    }

    static RepositoryNodeViewModel RepoNode(string workingDirectory)
        => new(new GitWizardRepository(workingDirectory));

    [Test]
    public void CopyToClipboard_CopiesWorkingDirectoryToClipboard()
    {
        var clipboard = new StubClipboardService();
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), clipboard);

        vm.CopyToClipboardCommand.Execute(RepoNode("C:/projects/widget"));

        Assert.That(clipboard.Writes, Does.Contain("C:/projects/widget"));
    }

    // The "Copied" modal dialog was replaced with a per-row indicator: copying must light a transient
    // flag on the node (which the view binds an icon to), not pop a dialog.
    [Test]
    public void CopyToClipboard_LightsTheRowCopiedIndicator()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());
        var node = RepoNode("C:/projects/widget");

        vm.CopyToClipboardCommand.Execute(node);

        // The stub clipboard completes synchronously and the stub dispatcher posts inline, so the
        // indicator is already lit when the fire-and-forget command returns - before the reset delay.
        Assert.That(node.JustCopied, Is.True);
    }

    // The indicator is transient: it lights on copy, then clears itself after the reset delay.
    [Test]
    public async Task CopyToClipboardAsync_ClearsTheCopiedIndicatorAfterTheDelay()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());
        var node = RepoNode("C:/projects/widget");

        var copy = vm.CopyToClipboardAsync(node);
        Assert.That(node.JustCopied, Is.True, "Indicator must light as soon as the copy completes.");

        await copy;
        Assert.That(node.JustCopied, Is.False, "Indicator must clear after the reset delay.");
    }

    [Test]
    public void CopyToClipboard_DoesNothingForNullOrGroupHeaderNodes()
    {
        var clipboard = new StubClipboardService();
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), clipboard);

        vm.CopyToClipboardCommand.Execute(null);
        vm.CopyToClipboardCommand.Execute(RepositoryNodeViewModel.CreateGroupHeader("C:"));

        Assert.That(clipboard.Writes, Is.Empty, "A null node and a group header (no working directory) must not copy.");
    }

    // A clipboard failure must propagate (so the AsyncRelayCommand wrapper logs it instead of leaving
    // it unobserved) and must not light a false "copied" indicator.
    [Test]
    public void CopyToClipboardAsync_FailsWithoutLightingIndicatorWhenClipboardThrows()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new ThrowingClipboardService());
        var node = RepoNode("C:/projects/widget");

        Assert.ThrowsAsync<InvalidOperationException>(() => vm.CopyToClipboardAsync(node));
        Assert.That(node.JustCopied, Is.False, "A failed clipboard write must not light the copied indicator.");
    }

    static MainViewModel CreateViewModel() =>
        new(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

    [Test]
    public void Commands_AreAllWired()
    {
        var vm = CreateViewModel();

        Assert.That(vm.OpenInExplorerCommand, Is.Not.Null);
        Assert.That(vm.OpenInForkCommand, Is.Not.Null);
        Assert.That(vm.CopyToClipboardCommand, Is.Not.Null);
        Assert.That(vm.DeepRefreshCommand, Is.Not.Null);
        Assert.That(vm.CheckoutMatchingBranchCommand, Is.Not.Null);
        Assert.That(vm.RefreshCommand, Is.Not.Null);
        Assert.That(vm.FetchAndRefreshCommand, Is.Not.Null);
        Assert.That(vm.CleanDownstreamCommand, Is.Not.Null);
    }

    [Test]
    public void CopyToClipboard_DoesNothing_ForNodeWithoutWorkingDirectory()
    {
        var clipboard = new StubClipboardService();
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), clipboard);
        var node = new RepositoryNodeViewModel(new GitWizardRepository(""));

        vm.CopyToClipboardCommand.Execute(node);

        Assert.That(clipboard.Writes, Is.Empty);
    }

    [Test]
    public void CanRefresh_IsFalse_WhileRefreshing()
    {
        var vm = CreateViewModel();
        Assert.That(vm.CanRefresh, Is.True);

        vm.IsRefreshing = true;

        Assert.That(vm.CanRefresh, Is.False);
    }

    [Test]
    public void Repositories_StartEmpty()
    {
        var vm = CreateViewModel();
        Assert.That(vm.Repositories, Is.Empty);
    }

    [Test]
    public void AfterRepositoriesSwap_InvokesTheAssignedAction()
    {
        var vm = CreateViewModel();
        var invoked = false;
        vm.AfterRepositoriesSwap = () => invoked = true;

        vm.AfterRepositoriesSwap!();

        Assert.That(invoked, Is.True);
    }

    [Test]
    public void SendUpdateMessage_UpdatesHeaderText()
    {
        var vm = CreateViewModel();

        vm.SendUpdateMessage("Scanning...");

        Assert.That(vm.HeaderText, Is.EqualTo("Scanning..."));
    }

    [Test]
    public void SendUpdateMessage_Null_LeavesHeaderTextUnchanged()
    {
        var vm = CreateViewModel();
        vm.SendUpdateMessage("original");

        vm.SendUpdateMessage(null);

        Assert.That(vm.HeaderText, Is.EqualTo("original"));
    }

    [Test]
    public void StartProgress_MakesProgressVisible()
    {
        var vm = CreateViewModel();

        vm.StartProgress("scanning", 10);

        Assert.That(vm.IsProgressVisible, Is.True);
        Assert.That(vm.ProgressText, Does.Contain("scanning"));
    }

    [Test]
    public void ApplyFilter_UnknownButton_LeavesFilterNone()
    {
        var vm = CreateViewModel();

        vm.ApplyFilter("NotARealButton");

        Assert.That(vm.ActiveFilter, Is.EqualTo(FilterType.None));
    }

    [Test]
    public void ApplyGroup_UnknownButton_LeavesGroupNone()
    {
        var vm = CreateViewModel();

        vm.ApplyGroup("NotARealButton");

        Assert.That(vm.ActiveGroupMode, Is.EqualTo(GroupMode.None));
    }

    [Test]
    public void ApplySort_UnknownButton_FallsBackToWorkingDirectory()
    {
        var vm = CreateViewModel();

        vm.ApplySort("NotARealButton");

        Assert.That(vm.ActiveSortMode, Is.EqualTo(SortMode.WorkingDirectory));
    }

    [Test]
    public void ApplyFilter_MapsEveryButtonNameToItsFilterType()
    {
        var vm = CreateViewModel();
        var cases = new (string Button, FilterType Expected)[]
        {
            ("FilterPendingChanges", FilterType.PendingChanges),
            ("FilterSubmoduleCheckout", FilterType.SubmoduleCheckout),
            ("FilterSubmoduleUninitialized", FilterType.SubmoduleUninitialized),
            ("FilterSubmoduleConfigIssue", FilterType.SubmoduleConfigIssue),
            ("FilterDetachedHead", FilterType.DetachedHead),
            ("FilterMyRepositories", FilterType.MyRepositories),
            ("FilterLocalOnlyCommits", FilterType.LocalOnlyCommits),
            ("FilterStale", FilterType.Stale),
            ("FilterDownstreamBranches", FilterType.DownstreamBranches),
        };

        foreach (var (button, expected) in cases)
        {
            // Each button differs from the previously active filter, so ToggleFilter sets (never clears) it.
            vm.ApplyFilter(button);
            Assert.That(vm.ActiveFilter, Is.EqualTo(expected), $"button {button}");
        }
    }

    [Test]
    public void ApplyGroup_MapsEveryButtonNameToItsGroupMode()
    {
        var vm = CreateViewModel();

        vm.ApplyGroup("GroupByDrive");
        Assert.That(vm.ActiveGroupMode, Is.EqualTo(GroupMode.Drive));

        vm.ApplyGroup("GroupByRemoteUrl");
        Assert.That(vm.ActiveGroupMode, Is.EqualTo(GroupMode.RemoteUrl));
    }

    [Test]
    public void ApplySort_MapsEveryButtonNameToItsSortMode()
    {
        var vm = CreateViewModel();

        vm.ApplySort("SortByRecentlyUsed");
        Assert.That(vm.ActiveSortMode, Is.EqualTo(SortMode.RecentlyUsed));

        vm.ApplySort("SortByRemoteUrl");
        Assert.That(vm.ActiveSortMode, Is.EqualTo(SortMode.RemoteUrl));

        vm.ApplySort("SortBySizeOnDisk");
        Assert.That(vm.ActiveSortMode, Is.EqualTo(SortMode.SizeOnDisk));

        vm.ApplySort("SortByWorkingDirectory");
        Assert.That(vm.ActiveSortMode, Is.EqualTo(SortMode.WorkingDirectory));
    }

    sealed class ThrowingClipboardService : IClipboardService
    {
        public Task SetPlainTextAsync(string text) => throw new InvalidOperationException("clipboard unavailable");
    }

    /// <summary>
    /// Test dispatcher backed by a real dedicated thread, so <see cref="IsOnUiThread"/> reflects
    /// genuine thread identity. Work is queued FIFO and pumped on that thread.
    /// </summary>
    sealed class PumpUiDispatcher : IUiDispatcher, IDisposable
    {
        readonly BlockingCollection<Action> _queue = new();
        readonly Thread _uiThread;

        public PumpUiDispatcher()
        {
            _uiThread = new Thread(Pump) { IsBackground = true, Name = "test-ui-thread" };
            _uiThread.Start();
        }

        void Pump()
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                // Swallow so a stray callback (e.g. the background UI-update loop firing after
                // disposal) can't tear down the pump thread mid-test; InvokeAsync surfaces its
                // own failures through the returned Task.
                try { action(); } catch { /* test pump stays alive */ }
            }
        }

        public bool IsOnUiThread => Thread.CurrentThread == _uiThread;

        public int UiThreadId => _uiThread.ManagedThreadId;

        public void Post(Action action)
        {
            if (!_queue.IsAddingCompleted)
                _queue.Add(action);
        }

        public Task InvokeAsync(Action action)
        {
            if (_queue.IsAddingCompleted)
                return Task.CompletedTask;

            var completion = new TaskCompletionSource();
            _queue.Add(() =>
            {
                try { action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
            });
            return completion.Task;
        }

        public Task InvokeAsync(Func<Task> action)
        {
            if (_queue.IsAddingCompleted)
                return Task.CompletedTask;

            var completion = new TaskCompletionSource();
            _queue.Add(() => _ = RunAndSignalAsync(action, completion));
            return completion.Task;
        }

        static async Task RunAndSignalAsync(Func<Task> action, TaskCompletionSource completion)
        {
            try { await action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        }

        public void Dispose() => _queue.CompleteAdding();
    }
}
