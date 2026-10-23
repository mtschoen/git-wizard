using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

// The UI command queue drains event-driven: the first enqueue of a burst starts one batching
// window on the injected TimeProvider, the window's expiry posts exactly one drain to the UI
// dispatcher, and DisposeAsync stops the window so nothing is scheduled afterwards.
public class MainViewModelUiDrainTests
{
    static MainViewModel CreateViewModel(RecordingUiDispatcher dispatcher, ControlledDelayClock clock) =>
        TestViewModels.Create(dispatcher: dispatcher, timeProvider: clock);

    static GitWizardRepository Repo(string workingDirectory) => new(workingDirectory);

    [Test]
    public async Task Construction_AndDispose_ScheduleNothing()
    {
        var dispatcher = new RecordingUiDispatcher();
        var clock = new ControlledDelayClock();
        var vm = CreateViewModel(dispatcher, clock);

        await vm.DisposeAsync();

        Assert.That(clock.CreatedTimerCount, Is.EqualTo(0));
        Assert.That(dispatcher.QueuedCount, Is.EqualTo(0));
    }

    [Test]
    public async Task EnqueueingManyCommandsInOneWindow_StartsOneWindowAndPostsOneDrain()
    {
        var dispatcher = new RecordingUiDispatcher();
        var clock = new ControlledDelayClock();
        var vm = CreateViewModel(dispatcher, clock);
        try
        {
            for (var index = 0; index < 20; index++)
                vm.OnRepositoryCreated(Repo($"C:/repos/repository-{index:00}"));

            Assert.That(clock.CreatedTimerCount, Is.EqualTo(1), "One batching window covers the whole burst.");
            Assert.That(clock.Timer!.Period, Is.EqualTo(Timeout.InfiniteTimeSpan), "The window is one-shot.");
            Assert.That(dispatcher.QueuedCount, Is.EqualTo(0), "Nothing is posted before the window expires.");
            Assert.That(vm.RepositoryList.Repositories, Is.Empty);

            clock.Timer.Fire();

            Assert.That(dispatcher.QueuedCount, Is.EqualTo(1), "Window expiry posts exactly one drain.");
            dispatcher.RunAll();
            Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(20));
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    [Test]
    public async Task EnqueueAfterDrain_StartsANewWindow()
    {
        var dispatcher = new RecordingUiDispatcher();
        var clock = new ControlledDelayClock();
        var vm = CreateViewModel(dispatcher, clock);
        try
        {
            vm.OnRepositoryCreated(Repo("C:/repos/alpha"));
            clock.Timer!.Fire();
            dispatcher.RunAll();

            vm.OnRepositoryCreated(Repo("C:/repos/beta"));

            Assert.That(clock.CreatedTimerCount, Is.EqualTo(2));
            clock.Timer.Fire();
            dispatcher.RunAll();
            Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(2));
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    [Test]
    public async Task ProgressUpdates_UseTheSameWindowAsCommands()
    {
        var dispatcher = new RecordingUiDispatcher();
        var clock = new ControlledDelayClock();
        var vm = CreateViewModel(dispatcher, clock);
        try
        {
            vm.StartProgress("Scanning", 4);
            dispatcher.RunAll();
            vm.OnRepositoryCreated(Repo("C:/repos/alpha"));
            vm.UpdateProgress(1);
            vm.UpdateProgress(2);

            Assert.That(clock.CreatedTimerCount, Is.EqualTo(1));
            clock.Timer!.Fire();
            Assert.That(dispatcher.QueuedCount, Is.EqualTo(1));
            dispatcher.RunAll();

            Assert.That(vm.ProgressText, Is.EqualTo("Scanning 2 / 4"));
            Assert.That(vm.ProgressValue, Is.EqualTo(0.5));
            Assert.That(vm.IsProgressVisible, Is.True);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    [Test]
    public async Task Dispose_StopsThePendingWindow_AndALateDrainDoesNothing()
    {
        var dispatcher = new RecordingUiDispatcher();
        var clock = new ControlledDelayClock();
        var vm = CreateViewModel(dispatcher, clock);
        vm.OnRepositoryCreated(Repo("C:/repos/alpha"));
        var window = clock.Timer!;

        await vm.DisposeAsync();

        Assert.That(window.IsDisposed, Is.True, "Dispose must stop the pending window.");
        window.Fire();
        dispatcher.RunAll();
        Assert.That(vm.RepositoryList.Repositories, Is.Empty, "A drain that was already posted must not run after dispose.");

        vm.OnRepositoryCreated(Repo("C:/repos/beta"));
        Assert.That(clock.CreatedTimerCount, Is.EqualTo(1), "No window starts after dispose.");
    }

    [Test]
    public async Task Dispose_WithAPostedDrain_MakesItANoOp()
    {
        var dispatcher = new RecordingUiDispatcher();
        var clock = new ControlledDelayClock();
        var vm = CreateViewModel(dispatcher, clock);
        vm.OnRepositoryCreated(Repo("C:/repos/alpha"));
        clock.Timer!.Fire();
        Assert.That(dispatcher.QueuedCount, Is.EqualTo(1), "Precondition: a drain is posted.");

        await vm.DisposeAsync();
        dispatcher.RunAll();

        Assert.That(vm.RepositoryList.Repositories, Is.Empty);
    }

    // Producer timing 1: the command is enqueued before disposal begins and is still pending.
    // Disposal discards it, and a drain forced afterwards applies nothing.
    [Test]
    public async Task Dispose_DiscardsCommandsStillPending()
    {
        var dispatcher = new RecordingUiDispatcher();
        var clock = new ControlledDelayClock();
        var vm = CreateViewModel(dispatcher, clock);
        vm.OnRepositoryCreated(Repo("C:/repos/alpha"));

        await vm.DisposeAsync();
        vm.DrainUICommands();

        Assert.That(vm.RepositoryList.Repositories, Is.Empty, "A pending command must not survive disposal.");
    }

    // Producer timing 2: disposal has begun (the drain is stopped) but DisposeAsync is still
    // running, held open inside the dispatcher. A command enqueued now is dropped, never thrown
    // into the producer, and never left in the queue.
    [Test]
    public async Task EnqueueAfterDisposalBegan_IsDroppedWithoutThrowing()
    {
        var dispatcher = new RecordingUiDispatcher { HoldInvokeAsync = true };
        var clock = new ControlledDelayClock();
        var vm = CreateViewModel(dispatcher, clock);

        var disposal = vm.DisposeAsync().AsTask();
        await dispatcher.InvokeEntered;

        Assert.DoesNotThrow(() => vm.OnRepositoryCreated(Repo("C:/repos/late")));
        Assert.That(clock.CreatedTimerCount, Is.EqualTo(0), "No window starts once disposal began.");

        dispatcher.ReleaseInvokeAsync();
        await disposal;
        vm.DrainUICommands();

        Assert.That(vm.RepositoryList.Repositories, Is.Empty, "A late command must not be applied.");
        Assert.That(await vm.WaitForUICommandsToDrainAsync(), Is.False, "The queue holds nothing to wait for.");
    }

    // The refresh's wait on the queue ends on disposal instead of polling a queue no drain empties.
    [Test]
    [CancelAfter(30000)]
    public async Task Dispose_EndsAnActiveWaitForTheQueueToDrain(CancellationToken testCancellation)
    {
        var dispatcher = new RecordingUiDispatcher();
        var clock = new ControlledDelayClock();
        var vm = CreateViewModel(dispatcher, clock);
        vm.OnRepositoryCreated(Repo("C:/repos/alpha"));
        var wait = vm.WaitForUICommandsToDrainAsync();
        Assert.That(wait.IsCompleted, Is.False, "Precondition: the wait is parked on the pending command.");

        await vm.DisposeAsync();

        Assert.That(await wait.WaitAsync(testCancellation), Is.False);
    }

    [Test]
    public async Task WaitForUICommandsToDrain_ReturnsTrueOnceADrainEmptiesTheQueue()
    {
        var dispatcher = new RecordingUiDispatcher();
        var clock = new ControlledDelayClock();
        var vm = CreateViewModel(dispatcher, clock);
        try
        {
            vm.OnRepositoryCreated(Repo("C:/repos/alpha"));
            var wait = vm.WaitForUICommandsToDrainAsync();
            Assert.That(wait.IsCompleted, Is.False);

            vm.DrainUICommands();
            clock.Timer!.Fire();

            Assert.That(await wait, Is.True);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    // Reports it is off the UI thread and queues every Post, so a test controls when each posted
    // drain runs. InvokeAsync runs inline so DisposeAsync completes without a pump, unless
    // HoldInvokeAsync parks it until ReleaseInvokeAsync so a test can act mid-disposal.
    sealed class RecordingUiDispatcher : IUiDispatcher
    {
        readonly Queue<Action> _queued = new();
        readonly TaskCompletionSource _invokeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _invokeReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HoldInvokeAsync { get; init; }
        public Task InvokeEntered => _invokeEntered.Task;
        public bool IsOnUiThread => true;
        public int QueuedCount => _queued.Count;
        public void Post(Action action) => _queued.Enqueue(action);
        public void ReleaseInvokeAsync() => _invokeReleased.TrySetResult();

        public async Task InvokeAsync(Action action)
        {
            if (HoldInvokeAsync)
            {
                _invokeEntered.TrySetResult();
                await _invokeReleased.Task.ConfigureAwait(false);
            }

            action();
        }

        public Task InvokeAsync(Func<Task> action) => action();

        public void RunAll()
        {
            while (_queued.TryDequeue(out var action))
                action();
        }
    }
}
