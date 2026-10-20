using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GitWizard;
using GitWizardTests.TestSupport;
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
        var swapCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.AfterRepositoriesSwap = () =>
        {
            Interlocked.Increment(ref swapCount.Value);
            swapCompleted.TrySetResult();
        };

        // Three distinct values, each fires the setter - but they arrive within the debounce window.
        vm.SearchText = "a";
        vm.SearchText = "ab";
        vm.SearchText = "abc";

        // Deferred, not immediate: the synchronous sets must not have run a filter pass yet.
        Assert.That(Volatile.Read(ref swapCount.Value), Is.EqualTo(0),
            "Filtering must be deferred during the debounce window, not run per keystroke.");

        // The callback runs after the deferred collection swap, so its completion is the condition
        // this test needs. The timeout only bounds a broken test instead of controlling progress.
        await swapCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(swapCount.Value, Is.EqualTo(1),
                "Rapid keystrokes must coalesce into a single filter/grouping pass.");
            Assert.That(vm.SearchText, Is.EqualTo("abc"));
        });
    }

    [Test]
    public async Task CancelAndDisposeAsync_DisposesOnlyAfterCancellationCallbacksComplete()
    {
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new CancellationTokenSource();
#pragma warning disable MA0045 // Synchronous block required inside cancellation callback to simulate in-flight callback
        await using var registration = source.Token.Register(() =>
        {
            callbackStarted.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        });
#pragma warning restore MA0045

        var cleanupTask = MainViewModel.CancelAndDisposeAsync(source);
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(cleanupTask.IsCompleted, Is.False,
                "Cleanup must wait for an in-flight cancellation callback.");
            Assert.DoesNotThrow(() => _ = source.Token,
                "The source must remain usable until cancellation callbacks finish.");
        });

        releaseCallback.TrySetResult();
        await cleanupTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Throws<ObjectDisposedException>(() => _ = source.Token);
    }

    // The sidebar buttons carry their FilterType/GroupMode in Tag and the code-behind routes a
    // click to ToggleFilter/ToggleGroupMode: re-clicking the active button clears it. The
    // sidebar's `.active` highlight binds to ActiveFilter/ActiveGroupMode, so this also pins
    // that those notifying properties (not just the private fields) move. The Tag-to-action
    // mapping itself is covered by the MainWindow sidebar tests.
    [Test]
    public void ToggleFilter_TogglesActiveFilterOnRepeatClick()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

        vm.ToggleFilter(FilterType.Stale);
        Assert.That(vm.ActiveFilter, Is.EqualTo(FilterType.Stale));

        vm.ToggleFilter(FilterType.Stale);
        Assert.That(vm.ActiveFilter, Is.EqualTo(FilterType.None), "Re-clicking the active filter must clear it.");

        vm.ToggleFilter(FilterType.PendingChanges);
        Assert.That(vm.ActiveFilter, Is.EqualTo(FilterType.PendingChanges), "Clicking a different filter must switch to it.");
    }

    [Test]
    public void ToggleGroupMode_TogglesActiveGroupModeOnRepeatClick()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

        vm.ToggleGroupMode(GroupMode.Drive);
        Assert.That(vm.ActiveGroupMode, Is.EqualTo(GroupMode.Drive));

        vm.ToggleGroupMode(GroupMode.Drive);
        Assert.That(vm.ActiveGroupMode, Is.EqualTo(GroupMode.None), "Re-clicking the active group must clear it.");
    }

    // Sort, unlike Filter/Group, has no "off" state - one mode is always active - so a repeat click
    // must keep it selected.
    [Test]
    public void SetSortMode_SetsModeAndDoesNotToggleOff()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

        vm.SetSortMode(SortMode.RecentlyUsed);
        Assert.That(vm.ActiveSortMode, Is.EqualTo(SortMode.RecentlyUsed));

        vm.SetSortMode(SortMode.RecentlyUsed);
        Assert.That(vm.ActiveSortMode, Is.EqualTo(SortMode.RecentlyUsed), "Re-clicking the active sort must keep it selected.");
    }

    [Test]
    public void ToggleFilter_RaisesPropertyChangedForActiveFilter()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

        var raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(MainViewModel.ActiveFilter);

        vm.ToggleFilter(FilterType.Stale);

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

        // The stub clipboard and dispatcher complete synchronously, so the write and the indicator
        // post have already run before the returned (still-pending) task is dropped.
        _ = vm.CopyToClipboardAsync(RepoNode("C:/projects/widget"));

        Assert.That(clipboard.Writes, Does.Contain("C:/projects/widget"));
    }

    // The "Copied" modal dialog was replaced with a per-row indicator: copying must light a transient
    // flag on the node (which the view binds an icon to), not pop a dialog.
    [Test]
    public void CopyToClipboard_LightsTheRowCopiedIndicator()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());
        var node = RepoNode("C:/projects/widget");

        _ = vm.CopyToClipboardAsync(node);

        // The stub clipboard completes synchronously and the stub dispatcher posts inline, so the
        // indicator is already lit when the copy task yields on its reset delay.
        Assert.That(node.JustCopied, Is.True);
    }

    [Test]
    public async Task CopyToClipboardAsync_ClearsTheCopiedIndicatorAfterTheDelay()
    {
        var clock = new ControlledDelayClock();
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(),
            new StubClipboardService(), clock);
        var node = RepoNode("C:/projects/widget");
        var copy = vm.CopyToClipboardAsync(node);
        try
        {
            Assert.That(clock.Timer, Is.Not.Null, "The indicator must use the injected clock.");
            Assert.That(node.JustCopied, Is.True);
            Assert.That(clock.Timer!.DueTime, Is.EqualTo(TimeSpan.FromMilliseconds(1500)));
            Assert.That(clock.Timer.Period, Is.EqualTo(Timeout.InfiniteTimeSpan));
            Assert.That(copy.IsCompleted, Is.False);
            clock.Timer.Fire();
            await copy;
            Assert.That(node.JustCopied, Is.False);
            Assert.That(clock.Timer.IsDisposed, Is.True);
        }
        finally
        {
            clock.Timer?.Fire();
            await copy;
        }
    }

    [Test]
    public async Task CopyToClipboard_DoesNothingForNullOrGroupHeaderNodes()
    {
        var clipboard = new StubClipboardService();
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), clipboard);

        await vm.CopyToClipboardAsync(null);
        await vm.CopyToClipboardAsync(RepositoryNodeViewModel.CreateGroupHeader("C:"));

        Assert.That(clipboard.Writes, Is.Empty, "A null node and a group header (no working directory) must not copy.");
    }

    // A clipboard failure must propagate to the caller (a code-behind fire-and-forget drop leaves
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
    public void ErrorsButtonLabel_ReflectsErrorLogCount()
    {
        var vm = CreateViewModel();

        Assert.That(vm.ErrorsButtonLabel, Is.EqualTo("Errors"),
            "With no errors logged, the button should not show a count.");

        vm.ErrorLog.AddError("boom");

        Assert.That(vm.ErrorsButtonLabel, Is.EqualTo("Errors (1)"));
    }

    [Test]
    public void ErrorsButtonLabel_ChangeRaisesPropertyChanged()
    {
        var vm = CreateViewModel();
        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, args) => raisedProperties.Add(args.PropertyName);

        vm.ErrorLog.AddError("boom");

        Assert.That(raisedProperties, Does.Contain(nameof(MainViewModel.ErrorsButtonLabel)));
    }

    [Test]
    public async Task CopyToClipboard_DoesNothing_ForNodeWithoutWorkingDirectory()
    {
        var clipboard = new StubClipboardService();
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), clipboard);
        var node = new RepositoryNodeViewModel(new GitWizardRepository(""));

        await vm.CopyToClipboardAsync(node);

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
        vm.IsScanning = true;

        vm.StartProgress("scanning", 10);

        Assert.That(vm.IsProgressVisible, Is.True);
        Assert.That(vm.ProgressText, Does.Contain("scanning"));
        Assert.That(vm.IsScanning, Is.False, "StartProgress must disable the scanning indicator.");
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
