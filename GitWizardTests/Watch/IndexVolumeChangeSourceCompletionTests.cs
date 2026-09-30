using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using static GitWizardTests.TestSupport.AsyncCondition;

namespace GitWizardTests.Watch;

/// <summary>
/// A drive reads CaughtUp only while MFTLib reads it CaughtUp: a fault that lands while a recovered
/// drive's reconciliation runs keeps the drive's observation pending, and a fault that lands just
/// after readiness was published corrects it through the ordinary fault path.
/// </summary>
[TestFixture]
public class IndexVolumeChangeSourceCompletionTests
{
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    [TestCase("drive")]
    [TestCase("apply")]
    public async Task StartupObservation_SupersededDuringReconciliation_StaysPending(string kind)
    {
        await using var scenario = await Scenario.CreateAsync(['X'], faultDuringFirstReconciliation: kind);
        var arming = scenario.Source.ArmAndCatchUpAsync(["X"], CancellationToken.None);

        await scenario.SecondFaultInjected.WaitAsync(HangGuard);
        var pollsBefore = scenario.Clock.TimersCreated;
        await WaitUntilAsync(() => scenario.Clock.TimersCreated > pollsBefore);

        Assert.That(arming.IsCompleted, Is.False, "The arm keeps following the newer recovery.");
        Assert.That(scenario.StateOf('X'), Is.EqualTo(WatchCatchUpState.Recovering));
        Assert.That(scenario.ReadinessOf('X'), Is.EqualTo(VolumeWatchState.CatchingUp));
        Assert.That(scenario.Violations, Is.Empty);

        scenario.ReleaseHeldRescan();
        var result = await arming.WaitAsync(HangGuard);
        Assert.That(result.Errors, Is.Empty);
        Assert.That(scenario.StateOf('X'), Is.EqualTo(WatchCatchUpState.CaughtUp));
        Assert.That(scenario.ReadinessOf('X'), Is.EqualTo(VolumeWatchState.CaughtUp));
        Assert.That(scenario.Violations, Is.Empty);
    }

    [Test]
    public async Task RecoveryObservation_FaultedAgainDuringReconciliation_DoesNotReadCaughtUp()
    {
        await using var scenario = await Scenario.CreateAsync(['X'], faultDuringFirstReconciliation: "drive",
            faultAfterStartup: false);
        await scenario.Source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        scenario.Scripted.FailDrive('X', new IOException("X failed"));

        await scenario.SecondFaultInjected.WaitAsync(HangGuard);
        var pollsBefore = scenario.Clock.TimersCreated;
        await WaitUntilAsync(() => scenario.Clock.TimersCreated > pollsBefore);

        Assert.That(scenario.StateOf('X'), Is.EqualTo(WatchCatchUpState.Recovering));
        Assert.That(scenario.ReadinessOf('X'), Is.EqualTo(VolumeWatchState.CatchingUp));
        Assert.That(scenario.Violations, Is.Empty);

        scenario.ReleaseHeldRescan();
        await WaitUntilAsync(() => scenario.ReadinessOf('X') == VolumeWatchState.CaughtUp);
        Assert.That(scenario.StateOf('X'), Is.EqualTo(WatchCatchUpState.CaughtUp));
        Assert.That(scenario.Violations, Is.Empty);
    }

    [Test]
    public async Task RecoveryObservation_ChannelLostDuringReconciliation_DoesNotReadCaughtUp()
    {
        await using var scenario = await Scenario.CreateAsync(['X', 'Y'], faultDuringFirstReconciliation: "channel",
            faultAfterStartup: false);
        await scenario.Source.ArmAndCatchUpAsync(["X", "Y"], CancellationToken.None);
        scenario.Scripted.FailDrive('X', new IOException("X failed"));

        await scenario.SecondFaultInjected.WaitAsync(HangGuard);
        await WaitUntilAsync(() => scenario.ReadinessOf('X') == VolumeWatchState.Excluded);

        Assert.That(scenario.StateOf('X'), Is.EqualTo(WatchCatchUpState.Faulted));
        Assert.That(scenario.Violations, Is.Empty);
        Assert.That(scenario.CaughtUpPublishedAfterSecondFault, Is.False);
    }

    [TestCase("drive", VolumeWatchState.CatchingUp)]
    [TestCase("channel", VolumeWatchState.Excluded)]
    public async Task FaultJustAfterCaughtUpWasPublished_IsCorrectedByTheFaultPath(string kind, VolumeWatchState expected)
    {
        await using var scenario = await Scenario.CreateAsync(['X', 'Y'], faultDuringFirstReconciliation: null,
            faultAfterStartup: false, faultOnRecoveredCaughtUp: kind);
        await scenario.Source.ArmAndCatchUpAsync(["X", "Y"], CancellationToken.None);
        scenario.Scripted.FailDrive('X', new IOException("X failed"));

        await scenario.SecondFaultInjected.WaitAsync(HangGuard);
        await WaitUntilAsync(() => scenario.ReadinessOf('X') == expected);
        Assert.That(scenario.StateOf('X'), Is.Not.EqualTo(WatchCatchUpState.CaughtUp));
    }

    /// <summary>
    /// One scripted index and source. The first rescan after the open is held until the drive has
    /// been seen recovering; the second is held until <see cref="ReleaseHeldRescan" />. A second fault is injected once, either while
    /// the first recovery's reconciliation runs or from inside the first recovered CaughtUp
    /// publication, and every CaughtUp publication records whether MFTLib read the drive CaughtUp.
    /// </summary>
    sealed class Scenario : IAsyncDisposable
    {
        readonly PackedIndexFixture _files = new();
        readonly TaskCompletionSource _heldRescan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _firstRescan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _secondFaultInjected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly List<string> _violations = [];
        int _productions;
        int _secondFault;
        int _caughtUpAfterSecondFault;
        int _startupFaulted;

        Scenario()
        {
        }

        public ScriptedIndexWatchSource Scripted { get; } = new();
        public ImmediateTimeProvider Clock { get; } = new();
        public MftIndexSession Owner { get; private set; } = null!;
        public IndexVolumeChangeSource Source { get; private set; } = null!;
        public Task SecondFaultInjected => _secondFaultInjected.Task;
        public bool CaughtUpPublishedAfterSecondFault => Volatile.Read(ref _caughtUpAfterSecondFault) != 0;

        public string[] Violations
        {
            get { lock (_violations) return _violations.ToArray(); }
        }

        public static async Task<Scenario> CreateAsync(char[] drives, string? faultDuringFirstReconciliation,
            bool faultAfterStartup = true, string? faultOnRecoveredCaughtUp = null)
        {
            var scenario = new Scenario();
            scenario._files.AddMarker("repo");
            var index = await scenario._files.OpenMftIndexAsync(scenario.Scripted, drives);
            scenario._files.BeforeProduceAsync = token =>
                (Interlocked.Increment(ref scenario._productions) == 1 ? scenario._firstRescan : scenario._heldRescan)
                .Task.WaitAsync(token);
            // The startup fault releases the first rescan itself once it has seen the drive recovering.
            if (!faultAfterStartup)
                scenario._firstRescan.SetResult();
            scenario.Owner = IndexVolumeChangeSourceTests.Owner(index, drives.Select(drive => drive.ToString()).ToArray());
            var owner = scenario.Owner;
            scenario.Source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner))
            {
                TimeProvider = scenario.Clock,
                StartupCatchUpSettledForTest = faultAfterStartup
                    ? scenario.FaultAfterStartupOnceAsync
                    : null,
                ReconciliationCapturedForTest = faultDuringFirstReconciliation is null
                    ? null
                    : drive => scenario.InjectSecondFault(drive, faultDuringFirstReconciliation)
            };
            scenario.Source.ReadinessChanged += snapshot =>
                scenario.OnReadinessChanged(snapshot, faultOnRecoveredCaughtUp);
            return scenario;
        }

        Task FaultAfterStartupOnceAsync(char drive)
        {
            if (Interlocked.Exchange(ref _startupFaulted, 1) != 0)
                return Task.CompletedTask;
            Scripted.FailDrive(drive, new IOException($"{drive} failed after its first catch-up"));
            return ReleaseFirstRescanWhenRecoveringAsync(drive);
        }

        async Task ReleaseFirstRescanWhenRecoveringAsync(char drive)
        {
            await WaitUntilAsync(() => StateOf(drive) == WatchCatchUpState.Recovering);
            _firstRescan.SetResult();
        }

        // Runs inside the source's delivery gate or its readiness publication, so it waits on
        // MFTLib's own state and never on the source's fault handler.
        void InjectSecondFault(char drive, string kind)
        {
            if (Interlocked.Exchange(ref _secondFault, 1) != 0)
                return;
            switch (kind)
            {
                case "drive":
                    Scripted.FailDrive(drive, new IOException($"{drive} failed again"));
                    break;
                case "apply":
                    Scripted.Publish(drive, new JournalBatch(null!, PackedIndexFixture.JournalId, 1002));
                    break;
                default:
                    Scripted.LoseChannel(drive, new IOException($"{drive} channel lost"));
                    break;
            }
            var settled = kind == "channel" ? WatchCatchUpState.Faulted : WatchCatchUpState.Recovering;
            Assert.That(SpinWait.SpinUntil(() => StateOf(drive) == settled, HangGuard), Is.True);
            _secondFaultInjected.TrySetResult();
        }

        void OnReadinessChanged(VolumeReadinessSnapshot snapshot, string? faultOnRecoveredCaughtUp)
        {
            if (!snapshot.TryGet("X", out var status) || status.State != VolumeWatchState.CaughtUp)
                return;
            var state = StateOf('X');
            if (state != WatchCatchUpState.CaughtUp)
            {
                lock (_violations)
                    _violations.Add($"CaughtUp published while MFTLib read {state}");
            }
            if (Volatile.Read(ref _secondFault) != 0)
                Volatile.Write(ref _caughtUpAfterSecondFault, 1);
            else if (faultOnRecoveredCaughtUp is not null && Volatile.Read(ref _productions) >= 1)
                InjectSecondFault('X', faultOnRecoveredCaughtUp);
        }

        public void ReleaseHeldRescan() => _heldRescan.TrySetResult();

        public WatchCatchUpState? StateOf(char drive) =>
            Owner.Index.Drives.FirstOrDefault(status => status.DriveLetter == drive)?.WatchCatchUp;

        public VolumeWatchState? ReadinessOf(char drive) =>
            Source.Readiness.TryGet(drive.ToString(), out var status) ? status.State : null;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Source.DisposeAsync();
            }
            finally
            {
                _files.Dispose();
            }
        }
    }
}
