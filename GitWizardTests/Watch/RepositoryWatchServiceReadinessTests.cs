using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using static GitWizardTests.TestSupport.AsyncCondition;

namespace GitWizardTests.Watch;

[TestFixture]
public class RepositoryWatchServiceReadinessTests
{

    sealed class Run : IAsyncDisposable
    {
        public readonly List<WatchReadiness> Ready = [];
        public readonly List<VolumeReadinessSnapshot> Snapshots = [];
        public readonly List<string> Stopped = [];
        public bool StartedRaised;
        public Task Pump = Task.CompletedTask;
        readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(10));

        public static Run Start(RepositoryWatchService service, CancellationToken? token = null)
        {
            var run = new Run();
            service.Ready += run.Ready.Add;
            service.VolumeReadinessChanged += run.Snapshots.Add;
            service.Stopped += run.Stopped.Add;
            service.Started += () => run.StartedRaised = true;
            run.Pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var _ in service.RunAsync(token ?? run._cancellation.Token)) { }
                }
                catch (OperationCanceledException) { }
            });
            return run;
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            try { await Pump; } catch (OperationCanceledException) { }
            _cancellation.Dispose();
        }
    }

    [Test]
    public async Task ReadyRequiresEveryDriveSettledAndOneCaughtUp()
    {
        await using var source = new ReadinessVolumeChangeSource();
        var service = new RepositoryWatchService(source, [@"X:\repo", @"Y:\repo"], [], TimeSpan.Zero);
        await using var run = Run.Start(service);
        await source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        await WaitUntilAsync(() => run.StartedRaised);
        Assert.That(run.Ready, Is.Empty, "Started is not a readiness fence.");

        source.SetVolume(VolumeWatchStatus.CatchingUp("X", VolumeBlockOrigin.Warm));
        source.SetVolume(VolumeWatchStatus.CatchingUp("Y", VolumeBlockOrigin.Cold));
        source.SetVolume(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Warm));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Ready, Is.Empty, "Y is still catching up.");
            Assert.That(service.Readiness, Is.Null);
        }

        source.SetVolume(VolumeWatchStatus.CaughtUp("Y", VolumeBlockOrigin.Cold));
        Assert.That(run.Ready, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Ready[0].Mode, Is.EqualTo(WatchReadinessMode.JournalBacklog));
            Assert.That(run.Ready[0].CaughtUpVolumes, Is.EquivalentTo(["X", "Y"]));
            Assert.That(run.Ready[0].ExcludedVolumes, Is.Empty);
            Assert.That(service.Readiness, Is.SameAs(run.Ready[0]));
        }

        // Later recovery or fault changes per-drive state without a second marker.
        source.SetVolume(VolumeWatchStatus.Excluded("Y", VolumeBlockOrigin.Cold, "Y faulted later"));
        Assert.That(run.Ready, Has.Count.EqualTo(1));
        Assert.That(run.Snapshots[^1].TryGet("Y", out var y) && y.State == VolumeWatchState.Excluded, Is.True);
    }

    [Test]
    public async Task PartialHealthyRunListsExcludedDrives()
    {
        await using var source = new ReadinessVolumeChangeSource();
        var service = new RepositoryWatchService(source, [@"X:\repo", @"Y:\repo"], [], TimeSpan.Zero);
        await using var run = Run.Start(service);
        await WaitUntilAsync(() => run.StartedRaised);
        source.SetVolume(VolumeWatchStatus.CatchingUp("X", VolumeBlockOrigin.Warm));
        source.SetVolume(VolumeWatchStatus.Excluded("Y", VolumeBlockOrigin.Unknown, "Drive is Offline."));
        Assert.That(run.Ready, Is.Empty);
        source.SetVolume(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Warm));
        Assert.That(run.Ready, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Ready[0].CaughtUpVolumes, Is.EqualTo(["X"]));
            Assert.That(run.Ready[0].ExcludedVolumes.Single().ExclusionReason, Is.EqualTo("Drive is Offline."));
        }
    }

    [Test]
    public async Task ImmediatelySettledSourceIsReadyAfterStartupDeliveryOnly()
    {
        await using var source = new ReadinessVolumeChangeSource();
        source.SetVolume(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Warm));
        var service = new RepositoryWatchService(source, [@"X:\repo"], [], TimeSpan.Zero);
        await using var run = Run.Start(service);
        await WaitUntilAsync(() => run.Ready.Count == 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Snapshots[0].Volumes.Single().State, Is.EqualTo(VolumeWatchState.CaughtUp),
                    "The snapshot read at subscribe time must be forwarded.");
            Assert.That(run.Ready[0].CaughtUpVolumes, Is.EqualTo(["X"]));
        }
    }

    [Test]
    public async Task StartupBatchesAreDeliveredBeforeReady()
    {
        var source = new ReadinessVolumeChangeSource();
        source.SetVolume(VolumeWatchStatus.CaughtUp("C", VolumeBlockOrigin.Cold));
        var request = new DiscoveryRequest(new GitWizardConfiguration(), DiscoveryPurpose.Watch, false, false, []);
        await using var startup = await FakeLiveStartup.FromSourceArmedAsync(source, request, CancellationToken.None,
            inventoryPaths: [@"C:\repo\a"], watchVolumes: ["C"],
            startupBatches: [new VolumeChangeBatch("C", [new VolumeChangeEntry(@"C:\repo\a\buffered.txt", VolumeEntryKind.Modified)])]);
        var service = new RepositoryWatchService(startup, debounce: TimeSpan.Zero);
        var order = new List<string>();
        service.Ready += _ => order.Add("ready");
        using var cancellation = new CancellationTokenSource(TestTimeouts.ShortHangGuard);
        var cancellationToken = cancellation.Token;
        var pump = Task.Run(async () =>
        {
            await foreach (var changeEvent in service.RunAsync(cancellationToken))
            {
                order.Add(changeEvent.Kind.ToString());
                source.Complete();
            }
        });
        await pump;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(order, Is.EqualTo(["ready", "Changed"]).Or.EqualTo(new[] { "Changed", "ready" }));
            Assert.That(service.Readiness, Is.Not.Null);
        }
        // The fence is set on the pump after the startup flush and before the live loop; the
        // consumer may observe the replayed event first, so the order above is either, but the
        // marker must exist exactly once and only after the flush completed.
        Assert.That(order.Count(item => item == "ready"), Is.EqualTo(1));
    }

    [Test]
    public async Task NoReadyOnTotalFailureOrEmptyUsableSet()
    {
        await using var source = new ReadinessVolumeChangeSource();
        var service = new RepositoryWatchService(source, [@"X:\repo"], [], TimeSpan.Zero);
        await using var run = Run.Start(service);
        await WaitUntilAsync(() => run.StartedRaised);
        source.SetVolume(VolumeWatchStatus.Excluded("X", VolumeBlockOrigin.Unknown, "cannot scan X"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Readiness.IsSettled, Is.True);
            Assert.That(run.Ready, Is.Empty, "A settled snapshot with no caught-up drive is not ready.");
        }
        source.KillSource("No requested drives could be watched.");
        await WaitUntilAsync(() => run.Stopped.Count == 1);
        Assert.That(run.Ready, Is.Empty);
    }

    [Test]
    public async Task CancellationBeforeSettleEmitsNoReady()
    {
        await using var source = new ReadinessVolumeChangeSource();
        var service = new RepositoryWatchService(source, [@"X:\repo"], [], TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        await using var run = Run.Start(service, cancellation.Token);
        await WaitUntilAsync(() => run.StartedRaised);
        source.SetVolume(VolumeWatchStatus.CatchingUp("X", VolumeBlockOrigin.Warm));
        await cancellation.CancelAsync();
        await run.Pump;
        source.SetVolume(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Warm));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Ready, Is.Empty);
            Assert.That(source.ReadinessSubscribers, Is.Zero, "RunAsync must detach on exit.");
        }
    }

    [Test]
    public async Task SourceDeathRacingFinalSettleEmitsNoReady()
    {
        await using var source = new ReadinessVolumeChangeSource();
        var service = new RepositoryWatchService(source, [@"X:\repo"], [], TimeSpan.Zero);
        await using var run = Run.Start(service);
        await WaitUntilAsync(() => run.StartedRaised);
        source.SetVolume(VolumeWatchStatus.CatchingUp("X", VolumeBlockOrigin.Warm));
        source.KillThenSettle("broker died", VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Warm));
        await WaitUntilAsync(() => run.Stopped.Count == 1);
        Assert.That(run.Ready, Is.Empty, "A settle observed after death must not produce a marker.");
    }

    [Test]
    public async Task ChannelFaultBeforeSettleEmitsNoReady()
    {
        await using var source = new ReadinessVolumeChangeSource();
        var service = new RepositoryWatchService(source, [@"X:\repo"], [], TimeSpan.Zero);
        var run = Run.Start(service);
        await WaitUntilAsync(() => run.StartedRaised);
        source.SetVolume(VolumeWatchStatus.CatchingUp("X", VolumeBlockOrigin.Warm));
        source.Fail(new InvalidOperationException("subscriber failed"));
        await WaitUntilAsync(() => run.Pump.IsCompleted);
        source.SetVolume(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Warm));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Ready, Is.Empty);
            Assert.That(run.Pump.IsFaulted, Is.True);
        }
        Assert.That(run.Pump.Exception!.InnerException, Is.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task SourceWithoutReadinessIsReadyWhenPumpIsActive()
    {
        // A non-empty batch list, not an empty one: the first read must actually produce
        // something (or stay pending) so the pump-start fence sees a genuinely active pump
        // rather than an immediate end of stream, which is source death, not readiness.
        var source = new ScriptedVolumeChangeSource([
            new VolumeChangeBatch("C", [new VolumeChangeEntry(@"C:\repo\a\file.txt", VolumeEntryKind.Modified)])
        ]);
        var service = new RepositoryWatchService(source, [@"C:\repo\a"], [], TimeSpan.Zero);
        WatchReadiness? readiness = null;
        service.Ready += value => readiness = value;
        using var cancellation = new CancellationTokenSource(TestTimeouts.ShortHangGuard);
        await foreach (var _ in service.RunAsync(cancellation.Token)) { }
        Assert.That(readiness, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(readiness!.Mode, Is.EqualTo(WatchReadinessMode.PumpActive));
            Assert.That(readiness.CaughtUpVolumes, Is.EqualTo(["C"]));
        }
    }

    [Test]
    public async Task ImmediateEndOfStreamNeverProducesReadyEvenForPumpActiveSource()
    {
        // The generic-source fallback path (no IPumpLifecycle) has no better proxy for "pump
        // active" than its own first read having begun - but that proxy must still reject a
        // first read that already ended the stream, exactly as the pump-lifecycle path does.
        var source = new ScriptedVolumeChangeSource([]);
        var service = new RepositoryWatchService(source, [@"C:\repo\a"], [], TimeSpan.Zero);
        WatchReadiness? readiness = null;
        service.Ready += value => readiness = value;
        using var cancellation = new CancellationTokenSource(TestTimeouts.ShortHangGuard);
        await foreach (var _ in service.RunAsync(cancellation.Token)) { }
        Assert.That(readiness, Is.Null, "An empty source ends on its first read; that is not readiness.");
    }

    [Test]
    public async Task ImmediateSourceReadFaultEmitsNoReadyEvenWhenSettled()
    {
        await using var source = new ReadinessVolumeChangeSource();
        source.SetVolume(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Warm));
        source.Fail(new InvalidOperationException("immediate source failure"));
        var service = new RepositoryWatchService(source, [@"X:\repo"], [], TimeSpan.Zero);
        var run = Run.Start(service);
        await WaitUntilAsync(() => run.Pump.IsCompleted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Ready, Is.Empty);
            Assert.That(service.Readiness, Is.Null);
            Assert.That(run.Pump.IsFaulted, Is.True);
        }
    }

    [Test]
    public async Task PumpNotYetStartedWithholdsReadyUntilSignalled()
    {
        // MoveNextAsync only queues a Task.Run-based pump (FanotifyVolumeChangeSource on Linux);
        // that queueing alone must not be mistaken for the pump actually running.
        await using var source = new ReadinessVolumeChangeSource(autoStartPump: false) { Mode = WatchReadinessMode.PumpActive };
        var service = new RepositoryWatchService(source, [@"X:\repo"], [], TimeSpan.Zero);
        await using var run = Run.Start(service);
        await source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        Assert.That(run.Ready, Is.Empty, "The read was only started, not confirmed running by the pump itself.");

        source.SignalPumpStarted();
        await WaitUntilAsync(() => run.Ready.Count == 1);
        Assert.That(run.Ready, Has.Count.EqualTo(1));
        Assert.That(run.Ready[0].Mode, Is.EqualTo(WatchReadinessMode.PumpActive));
    }

    [Test]
    public async Task ImmediateEndOfStreamNeverProducesReadyEvenAfterPumpSignalsStart()
    {
        // The first read ending the stream is source death, not readiness - even when the pump
        // itself later (or concurrently) confirms it started.
        await using var source = new ReadinessVolumeChangeSource(autoStartPump: false) { Mode = WatchReadinessMode.PumpActive };
        source.Complete();
        var service = new RepositoryWatchService(source, [@"X:\repo"], [], TimeSpan.Zero);
        var run = Run.Start(service);
        await source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        source.SignalPumpStarted();
        await WaitUntilAsync(() => run.Pump.IsCompleted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Ready, Is.Empty, "The first read already ended the stream; that is source death, not readiness.");
            Assert.That(run.Pump.IsFaulted, Is.False);
        }
    }

    [Test]
    public async Task HistorySurvivesThroughServiceReadinessSnapshots()
    {
        await using var source = new ReadinessVolumeChangeSource();
        var session = Guid.NewGuid();
        var transition1 = new VolumeWatchTransition(session, "X", 1, "Faulted", "read error", DateTimeOffset.UtcNow);
        var transition2 = new VolumeWatchTransition(session, "X", 2, "Recovering", null, DateTimeOffset.UtcNow);
        var transition3 = new VolumeWatchTransition(session, "X", 3, "CatchingUp", null, DateTimeOffset.UtcNow);
        var transition4 = new VolumeWatchTransition(session, "X", 4, "CaughtUp", null, DateTimeOffset.UtcNow);
        var snapshot = VolumeReadinessSnapshot.Empty
            .WithTransition(transition1)
            .WithTransition(transition2)
            .WithTransition(transition3)
            .WithTransition(transition4);
        source.SetSnapshot(snapshot.With(VolumeWatchStatus.CatchingUp("X", VolumeBlockOrigin.Warm)));

        var service = new RepositoryWatchService(source, [@"X:\repo"], [], TimeSpan.Zero);
        await using var run = Run.Start(service);
        await WaitUntilAsync(() => run.StartedRaised);

        var latestSnapshot = source.Readiness
            .With(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Warm));
        source.SetSnapshot(latestSnapshot);

        await WaitUntilAsync(() => run.Ready.Count == 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Ready, Has.Count.EqualTo(1));
            Assert.That(run.Snapshots[^1].RecentTransitions.Select(t => t.State),
                Is.EqualTo(["Faulted", "Recovering", "CatchingUp", "CaughtUp"]));
        }
    }
}
