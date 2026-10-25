using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using GitWizardTests.TestSupport;
using GitWizardUI.Services;

namespace GitWizardTests.UI;

public class DispatcherWatchdogTests
{
    [Test]
    public void Inspect_WhenCallbackExceedsThreshold_ReportsObservation()
    {
        var clock = new ControlledDelayClock();
        var observations = new List<DispatcherWatchdogObservation>();
        var watchdog = new DispatcherWatchdog(clock, TimeSpan.FromMilliseconds(500), observations.Add);
        var inspected = watchdog.Inspect("refresh", () => clock.Advance(TimeSpan.FromMilliseconds(501)));

        inspected();

        Assert.That(observations, Is.EqualTo(new[]
        {
            new DispatcherWatchdogObservation("refresh", TimeSpan.FromMilliseconds(501),
                TimeSpan.FromMilliseconds(500))
        }));
    }

    [Test]
    public void InspectFunc_WhenCallbackExceedsThreshold_ReportsObservation()
    {
        var clock = new ControlledDelayClock();
        var observations = new List<DispatcherWatchdogObservation>();
        var watchdog = new DispatcherWatchdog(clock, TimeSpan.FromMilliseconds(500), observations.Add);
        var inspected = watchdog.Inspect("refresh", () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(501));
            return 42;
        });

        Assert.That(inspected(), Is.EqualTo(42));
        Assert.That(observations, Is.EqualTo(new[]
        {
            new DispatcherWatchdogObservation("refresh", TimeSpan.FromMilliseconds(501),
                TimeSpan.FromMilliseconds(500))
        }));
    }

    [Test]
    public void Inspect_WhenCallbackWithinThreshold_ReportsNothing()
    {
        var clock = new ControlledDelayClock();
        var observations = new List<DispatcherWatchdogObservation>();
        var watchdog = new DispatcherWatchdog(clock, TimeSpan.FromMilliseconds(500), observations.Add);
        var inspected = watchdog.Inspect("refresh", () => clock.Advance(TimeSpan.FromMilliseconds(500)));

        inspected();

        Assert.That(observations, Is.Empty);
    }

    [Test]
    public void InspectFunc_WhenCallbackWithinThreshold_ReportsNothing()
    {
        var clock = new ControlledDelayClock();
        var observations = new List<DispatcherWatchdogObservation>();
        var watchdog = new DispatcherWatchdog(clock, TimeSpan.FromMilliseconds(500), observations.Add);
        var inspected = watchdog.Inspect("refresh", () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            return 42;
        });

        Assert.That(inspected(), Is.EqualTo(42));
        Assert.That(observations, Is.Empty);
    }

    [Test]
    public void Inspect_WhenCallbackThrows_StillReportsSlowExecution()
    {
        var clock = new ControlledDelayClock();
        var observations = new List<DispatcherWatchdogObservation>();
        var watchdog = new DispatcherWatchdog(clock, TimeSpan.FromMilliseconds(500), observations.Add);
        var inspected = watchdog.Inspect("refresh", () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(600));
            throw new InvalidOperationException("callback failed");
        });

        Assert.Throws<InvalidOperationException>(() => inspected());
        Assert.That(observations, Is.EqualTo(new[]
        {
            new DispatcherWatchdogObservation("refresh", TimeSpan.FromMilliseconds(600),
                TimeSpan.FromMilliseconds(500))
        }));
    }

    [Test]
    public void InspectFunc_WhenCallbackThrows_StillReportsSlowExecution()
    {
        var clock = new ControlledDelayClock();
        var observations = new List<DispatcherWatchdogObservation>();
        var watchdog = new DispatcherWatchdog(clock, TimeSpan.FromMilliseconds(500), observations.Add);
        var inspected = watchdog.Inspect<int>("refresh", () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(600));
            throw new InvalidOperationException("callback failed");
        });

        Assert.Throws<InvalidOperationException>(() => inspected());
        Assert.That(observations, Is.EqualTo(new[]
        {
            new DispatcherWatchdogObservation("refresh", TimeSpan.FromMilliseconds(600),
                TimeSpan.FromMilliseconds(500))
        }));
    }

    [Test]
    public void Wrap_Action_InvokesCallback()
    {
        var invoked = false;
        var wrapped = DispatcherWatchdog.Wrap("refresh", () => { invoked = true; });

        wrapped();

        Assert.That(invoked, Is.True);
    }

    [Test]
    public void Wrap_Func_ReturnsCallbackResult()
    {
        var wrapped = DispatcherWatchdog.Wrap("refresh", () => 42);

        Assert.That(wrapped(), Is.EqualTo(42));
    }

    [AvaloniaTest]
    public void Post_UsesCallerMemberNameForObservation()
    {
        var clock = new ControlledDelayClock();
        var observations = new List<DispatcherWatchdogObservation>();
        var watchdog = new DispatcherWatchdog(clock, TimeSpan.FromMilliseconds(500), observations.Add);
        var dispatcher = new AvaloniaUiDispatcher(watchdog);

        dispatcher.Post(() => clock.Advance(TimeSpan.FromMilliseconds(501)));
        Dispatcher.UIThread.RunJobs();

        Assert.That(observations, Is.EqualTo(new[]
        {
            new DispatcherWatchdogObservation(nameof(Post_UsesCallerMemberNameForObservation),
                TimeSpan.FromMilliseconds(501), TimeSpan.FromMilliseconds(500))
        }));
    }

    [AvaloniaTest]
    public void InvokeAsync_Action_UsesCallerMemberNameForObservation()
    {
        var clock = new ControlledDelayClock();
        var observations = new List<DispatcherWatchdogObservation>();
        var watchdog = new DispatcherWatchdog(clock, TimeSpan.FromMilliseconds(500), observations.Add);
        var dispatcher = new AvaloniaUiDispatcher(watchdog);

        var invocation = dispatcher.InvokeAsync(() => clock.Advance(TimeSpan.FromMilliseconds(501)));
        Dispatcher.UIThread.RunJobs();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(invocation.IsCompleted, Is.True);
            Assert.That(observations, Is.EqualTo(new[]
            {
            new DispatcherWatchdogObservation(nameof(InvokeAsync_Action_UsesCallerMemberNameForObservation),
                TimeSpan.FromMilliseconds(501), TimeSpan.FromMilliseconds(500))
        }));
        }
    }

    [AvaloniaTest]
    public async Task InvokeAsync_FuncTask_MeasuresOnlySynchronousSegment()
    {
        var clock = new ControlledDelayClock();
        var observations = new List<DispatcherWatchdogObservation>();
        var watchdog = new DispatcherWatchdog(clock, TimeSpan.FromMilliseconds(500), observations.Add);
        var dispatcher = new AvaloniaUiDispatcher(watchdog);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var invocation = dispatcher.InvokeAsync(async () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(600));
            await release.Task;
            clock.Advance(TimeSpan.FromMilliseconds(600));
        });
        Dispatcher.UIThread.RunJobs();

        Assert.That(observations, Is.EqualTo(new[]
        {
            new DispatcherWatchdogObservation(nameof(InvokeAsync_FuncTask_MeasuresOnlySynchronousSegment),
                TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(500))
        }));

        release.SetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.That(invocation.IsCompleted, Is.True);
        await invocation;
        Assert.That(observations, Has.Count.EqualTo(1));
    }
}
