using GitWizard.Watch;
using GitWizardUI.ViewModels;

namespace GitWizardTests.UI;

[TestFixture]
public class LiveWatchHistoryViewModelTests
{
    sealed class HistoryClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public HistoryTimer? Timer { get; private set; }
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period) => Timer = new HistoryTimer(callback, state);
    }

    sealed class HistoryTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool IsDisposed { get; private set; }
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
        public void Dispose() => IsDisposed = true;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public void FaultAndRecoveryRemainSeparateUntilTheyExpire()
    {
        var clock = new HistoryClock();
        using var history = new LiveWatchHistoryViewModel(action => action(), clock);
        var session = Guid.NewGuid();
        var events = new[]
        {
            new VolumeWatchTransition(session, "X", 4, "Faulted", "read failed", clock.Now),
            new VolumeWatchTransition(session, "X", 5, "Recovering", "read failed", clock.Now)
        };
        history.Apply(events);
        history.Apply(events);
        Assert.That(history.Events.Select(item => item.State), Is.EqualTo(["Faulted", "Recovering"]));
        clock.Now += TimeSpan.FromSeconds(29);
        history.Expire();
        Assert.That(history.Events, Has.Count.EqualTo(2));
        clock.Now += TimeSpan.FromSeconds(1);
        history.Expire();
        Assert.That(history.Events, Is.Empty);
        history.Apply(events);
        Assert.That(history.Events, Is.Empty, "Snapshot replay cannot resurrect expired events.");
    }

    [Test]
    public void CapacityIsBoundedToSixtyFourEntries()
    {
        var clock = new HistoryClock();
        using var history = new LiveWatchHistoryViewModel(action => action(), clock);
        var session = Guid.NewGuid();
        var events = Enumerable.Range(1, 70).Select(version =>
            new VolumeWatchTransition(session, "X", version, "Recovering", null, clock.Now)).ToList();
        history.Apply(events);
        Assert.That(history.Events, Has.Count.EqualTo(64));
        Assert.That(history.Events.Select(item => item.Version),
            Is.EqualTo(Enumerable.Range(7, 64).Select(value => (long)value)));
    }

    [Test]
    public void NewSessionAcceptsVersionOneAfterPriorSessionHigherVersions()
    {
        var clock = new HistoryClock();
        using var history = new LiveWatchHistoryViewModel(action => action(), clock);
        var firstSession = Guid.NewGuid();
        var firstSessionEvents = Enumerable.Range(1, 70).Select(version =>
            new VolumeWatchTransition(firstSession, "X", version, "Recovering", null, clock.Now)).ToList();
        history.Apply(firstSessionEvents);

        var secondSession = Guid.NewGuid();
        var newSessionEvent = new VolumeWatchTransition(secondSession, "X", 1, "Faulted", "new session", clock.Now);
        history.Apply([newSessionEvent]);

        Assert.That(history.Events[^1].SessionId, Is.EqualTo(secondSession));
        Assert.That(history.Events[^1].Version, Is.EqualTo(1));
    }

    [Test]
    public void TimerFiresExpiryThroughDispatcherAndStopsWhenEmptyOrDisposed()
    {
        var clock = new HistoryClock();
        var dispatched = new List<Action>();
        using var history = new LiveWatchHistoryViewModel(dispatched.Add, clock);
        var session = Guid.NewGuid();
        var transition = new VolumeWatchTransition(session, "X", 1, "Faulted", "error", clock.Now);

        history.Apply([transition]);
        Assert.That(clock.Timer, Is.Not.Null);
        Assert.That(history.Events, Has.Count.EqualTo(1));

        clock.Timer!.Fire();
        Assert.That(dispatched, Has.Count.EqualTo(1));

        dispatched[0]();
        dispatched.Clear();
        Assert.That(history.Events, Has.Count.EqualTo(1));

        clock.Now += TimeSpan.FromSeconds(31);
        clock.Timer.Fire();
        Assert.That(dispatched, Has.Count.EqualTo(1));
        dispatched[0]();
        dispatched.Clear();
        Assert.That(history.Events, Is.Empty);
        Assert.That(clock.Timer!.IsDisposed, Is.True, "Timer stops when empty.");

        var nextTransition = new VolumeWatchTransition(session, "X", 2, "Recovering", null, clock.Now);
        history.Apply([nextTransition]);
        var secondTimer = clock.Timer;
        Assert.That(secondTimer, Is.Not.Null);

        secondTimer!.Fire();
        Assert.That(dispatched, Has.Count.EqualTo(1));
        var queuedAction = dispatched[0];
        dispatched.Clear();

        history.Clear();
        Assert.That(history.Events, Is.Empty);

        queuedAction();
        Assert.That(history.Events, Is.Empty);
    }
}
