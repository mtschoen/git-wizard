using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;
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

    [TestCase("drive")]
    [TestCase("apply")]
    public async Task StartupObservation_SupersededDuringReconciliation_StaysPending(string kind)
    {
        await using var scenario = await Scenario.CreateAsync(['X'], faultDuringFirstReconciliation: kind);
        var arming = scenario.Source.ArmAndCatchUpAsync(["X"], CancellationToken.None);

        await scenario.SecondFaultInjected.WaitAsync(TestTimeouts.HangGuard);
        var productionsBefore = scenario.ProductionsAtSecondFault;
        await WaitUntilAsync(() => scenario.Productions > productionsBefore);

        Assert.That(arming.IsCompleted, Is.False, "The arm keeps following the newer recovery.");
        Assert.That(scenario.StateOf('X'), Is.EqualTo(WatchState.Recovering));
        Assert.That(scenario.ReadinessOf('X'), Is.EqualTo(VolumeWatchState.CatchingUp));
        Assert.That(scenario.Violations, Is.Empty);

        var releaseIssued = new TaskCompletionSource();
        var completedBeforeRelease = arming.ContinueWith(_ => !releaseIssued.Task.IsCompleted,
            TaskScheduler.Default);
        releaseIssued.SetResult();
        scenario.ReleaseHeldRescan();
        var result = await arming.WaitAsync(TestTimeouts.HangGuard);
        Assert.That(await completedBeforeRelease.WaitAsync(TestTimeouts.HangGuard), Is.False,
            "The arm must not complete before the newer recovery finishes.");
        Assert.That(result.Errors, Is.Empty);
        Assert.That(scenario.StateOf('X'), Is.EqualTo(WatchState.CaughtUp));
        Assert.That(scenario.ReadinessOf('X'), Is.EqualTo(VolumeWatchState.CaughtUp));
        Assert.That(scenario.Violations, Is.Empty);
    }

    [Test]
    public async Task RecoveryObservation_FaultedAgainDuringReconciliation_DoesNotReadCaughtUp()
    {
        await using var scenario = await Scenario.CreateAsync(['X'], faultDuringFirstReconciliation: "drive",
            faultAfterStartup: false);
        await scenario.Source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        scenario.Scripted.WatchFor('X').FailDrive(new IOException("X failed"));

        await scenario.SecondFaultInjected.WaitAsync(TestTimeouts.HangGuard);
        var productionsBefore = scenario.ProductionsAtSecondFault;
        await WaitUntilAsync(() => scenario.Productions > productionsBefore);

        Assert.That(scenario.StateOf('X'), Is.EqualTo(WatchState.Recovering));
        Assert.That(scenario.ReadinessOf('X'), Is.EqualTo(VolumeWatchState.CatchingUp));
        Assert.That(scenario.Violations, Is.Empty);

        scenario.ReleaseHeldRescan();
        await WaitUntilAsync(() => scenario.ReadinessOf('X') == VolumeWatchState.CaughtUp);
        Assert.That(scenario.StateOf('X'), Is.EqualTo(WatchState.CaughtUp));
        Assert.That(scenario.Violations, Is.Empty);
    }

    [Test]
    public async Task RecoveryObservation_ChannelLostDuringReconciliation_DoesNotReadCaughtUp()
    {
        await using var scenario = await Scenario.CreateAsync(['X', 'Y'], faultDuringFirstReconciliation: "channel",
            faultAfterStartup: false);
        await scenario.Source.ArmAndCatchUpAsync(["X", "Y"], CancellationToken.None);
        scenario.Scripted.WatchFor('X').FailDrive(new IOException("X failed"));

        await scenario.SecondFaultInjected.WaitAsync(TestTimeouts.HangGuard);
        await WaitUntilAsync(() => scenario.ReadinessOf('X') == VolumeWatchState.Excluded);

        Assert.That(scenario.StateOf('X'), Is.EqualTo(WatchState.Faulted));
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
        scenario.Scripted.WatchFor('X').FailDrive(new IOException("X failed"));

        await scenario.SecondFaultInjected.WaitAsync(TestTimeouts.HangGuard);
        await WaitUntilAsync(() => scenario.ReadinessOf('X') == expected);
        Assert.That(scenario.StateOf('X'), Is.Not.EqualTo(WatchState.CaughtUp));
    }

    [Test]
    public async Task NewerRecoverySupersedesOlderAndDuplicateCaughtUp()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = IndexVolumeChangeSourceTests.Owner(
            await files.OpenMftIndexAsync(scripted, 'X'), "X");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        var version = owner.Index.Drives.Single().Watch.StateVersion + 10;
        var fault = SyntheticNotifications.CreateWatchFault(WatchFaultKind.Drive, 'X', new IOException("recovering"));
        var watch = owner.Index.Drives.Single().Watch;
        source.ApplyWatchState(SyntheticNotifications.CreateWatchStateChange('X', watch with { State = WatchState.Recovering, StateVersion = version }, fault));
        source.ApplyWatchState(SyntheticNotifications.CreateWatchStateChange('X', watch with { State = WatchState.CaughtUp, StateVersion = version - 1 }, null));
        source.ApplyWatchState(SyntheticNotifications.CreateWatchStateChange('X', watch with { State = WatchState.CaughtUp, StateVersion = version }, null));
        Assert.That(source.Readiness.TryGet("X", out var status), Is.True);
        Assert.That(status!.State, Is.EqualTo(VolumeWatchState.CatchingUp));
        Assert.That(status.WatchStateVersion, Is.EqualTo(version));
        Assert.That(source.Readiness.RecentTransitions[^1].State, Is.EqualTo("Recovering"));
        Assert.That(source.Readiness.RecentTransitions.Count(item => item.Version == version), Is.EqualTo(1));
    }

    [Test]
    public async Task TwoDriveVersionsAreIndependent()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = IndexVolumeChangeSourceTests.Owner(
            await files.OpenMftIndexAsync(scripted, 'X', 'Y'), "X", "Y");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X", "Y"], CancellationToken.None);
        var versionX = owner.Index.Drives.Single(d => d.Drive == 'X').Watch.StateVersion;
        var versionY = owner.Index.Drives.Single(d => d.Drive == 'Y').Watch.StateVersion;
        var watchX = owner.Index.Drives.Single(drive => drive.Drive == 'X').Watch;
        var watchY = owner.Index.Drives.Single(drive => drive.Drive == 'Y').Watch;
        source.ApplyWatchState(SyntheticNotifications.CreateWatchStateChange('X', watchX with { State = WatchState.Recovering, StateVersion = versionX + 100 },
            SyntheticNotifications.CreateWatchFault(WatchFaultKind.Drive, 'X', new IOException("X recovering"))));
        source.ApplyWatchState(SyntheticNotifications.CreateWatchStateChange('Y', watchY with { State = WatchState.Recovering, StateVersion = versionY + 1 },
            SyntheticNotifications.CreateWatchFault(WatchFaultKind.Drive, 'Y', new IOException("Y recovering"))));
        Assert.That(source.Readiness.TryGet("X", out var statusX), Is.True);
        Assert.That(statusX!.WatchStateVersion, Is.EqualTo(versionX + 100));
        Assert.That(source.Readiness.TryGet("Y", out var statusY), Is.True);
        Assert.That(statusY!.WatchStateVersion, Is.EqualTo(versionY + 1));
    }

    [Test]
    public async Task OlderEventIgnoredAfterNewerSeedOrEvent()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = IndexVolumeChangeSourceTests.Owner(
            await files.OpenMftIndexAsync(scripted, 'X'), "X");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        var initialVersion = owner.Index.Drives.Single().Watch.StateVersion;
        var intermediateVersion = initialVersion + 5;
        var watch = owner.Index.Drives.Single().Watch;
        source.ApplyWatchState(SyntheticNotifications.CreateWatchStateChange('X', watch with { State = WatchState.Recovering, StateVersion = intermediateVersion },
            SyntheticNotifications.CreateWatchFault(WatchFaultKind.Drive, 'X', new IOException("recovering"))));
        var countBefore = source.Readiness.RecentTransitions.Count;

        // Deliver older event: should be ignored
        source.ApplyWatchState(SyntheticNotifications.CreateWatchStateChange('X', watch with { State = WatchState.CaughtUp, StateVersion = intermediateVersion - 1 }, null));
        Assert.That(source.Readiness.RecentTransitions.Count, Is.EqualTo(countBefore));
        Assert.That(source.Readiness.TryGet("X", out var status), Is.True);
        Assert.That(status!.WatchStateVersion, Is.EqualTo(intermediateVersion));

        // Deliver strictly newer event: should be accepted
        var newerVersion = intermediateVersion + 1;
        source.ApplyWatchState(SyntheticNotifications.CreateWatchStateChange('X', watch with { State = WatchState.CaughtUp, StateVersion = newerVersion }, null));
        await WaitUntilAsync(() => source.Readiness.TryGet("X", out var s) && s.WatchStateVersion == newerVersion &&
            s.State == VolumeWatchState.CaughtUp);
        Assert.That(source.Readiness.TryGet("X", out var updated), Is.True);
        Assert.That(updated!.State, Is.EqualTo(VolumeWatchState.CaughtUp));
        Assert.That(updated.WatchStateVersion, Is.EqualTo(newerVersion));
        Assert.That(source.Readiness.RecentTransitions.Count, Is.EqualTo(countBefore + 1));
    }

    [Test]
    public async Task ReconciliationJob_SupersededByNewerVersion_DoesNotPublishOlderCaughtUp()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("oldrepo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = IndexVolumeChangeSourceTests.Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeProduceAsync = (_, token) => release.Task.WaitAsync(token);

        long next = 0;
        var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        source.ReconciliationCapturedForTest = drive =>
        {
            next = owner.Index.Drives.Single(item => item.Drive == drive).Watch.StateVersion + 1;
            var watch = owner.Index.Drives.Single(item => item.Drive == drive).Watch;
            source.ApplyWatchState(SyntheticNotifications.CreateWatchStateChange(drive, watch with { State = WatchState.Recovering, StateVersion = next },
                SyntheticNotifications.CreateWatchFault(WatchFaultKind.Drive, drive, new IOException("newer recovery"))));
        };
        await using var sourceLifetime = source;
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        scripted.WatchFor('X').FailDrive(new IOException("X failed"));
        await WaitUntilAsync(() => owner.Index.Drives.Single().Watch.State == WatchState.Recovering);
        files.AddMarker("newrepo");
        release.SetResult();

        // The reconciliation batch is delivered
        Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(TestTimeouts.HangGuard), Is.True);
        Assert.That(reader.Current.Entries, Does.Contain(new VolumeChangeEntry(@"X:\newrepo\.git", VolumeEntryKind.Created)));

        // The source still reads CatchingUp at next, not CaughtUp at the older version
        Assert.That(source.Readiness.TryGet("X", out var status), Is.True);
        Assert.That(status!.State, Is.EqualTo(VolumeWatchState.CatchingUp));
        Assert.That(status.WatchStateVersion, Is.EqualTo(next));

        // Submit CaughtUp at next + 1 and wait for that exact published version
        source.ApplyWatchState(SyntheticNotifications.CreateWatchStateChange('X', owner.Index.Drives.Single().Watch with { State = WatchState.CaughtUp, StateVersion = next + 1 }, null));
        await WaitUntilAsync(() => source.Readiness.TryGet("X", out var s) && s.WatchStateVersion == next + 1 &&
            s.State == VolumeWatchState.CaughtUp);
        Assert.That(source.Readiness.TryGet("X", out var caughtUp), Is.True);
        Assert.That(caughtUp!.State, Is.EqualTo(VolumeWatchState.CaughtUp));
        Assert.That(caughtUp.WatchStateVersion, Is.EqualTo(next + 1));
    }

    [Test]
    public void HistorySurvivesSiblingUpdatesAndIsBounded()
    {
        var session = Guid.NewGuid();
        var snapshot = VolumeReadinessSnapshot.Empty;
        for (var version = 1; version <= 70; version++)
        {
            snapshot = snapshot.WithTransition(new VolumeWatchTransition(
                session, "X", version, "Recovering", null, DateTimeOffset.UnixEpoch));
        }

        var previous = snapshot;
        snapshot = snapshot.With(VolumeWatchStatus.CaughtUp("Y", VolumeBlockOrigin.Cold));
        Assert.That(snapshot.RecentTransitions.Select(item => item.Version),
            Is.EqualTo(Enumerable.Range(7, 64).Select(value => (long)value)));
        Assert.That(previous.Volumes, Is.Empty);
        Assert.That(snapshot.RecentTransitions, Is.EqualTo(previous.RecentTransitions));
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
        int _productionsAtSecondFault;
        int _secondFault;
        int _caughtUpAfterSecondFault;
        int _startupFaulted;

        Scenario()
        {
        }

        public ScriptedWatchSource Scripted { get; } = new() { CatchUpOnStart = true };
        public MftIndexSession Owner { get; private set; } = null!;
        public IndexVolumeChangeSource Source { get; private set; } = null!;
        public Task SecondFaultInjected => _secondFaultInjected.Task;
        public bool CaughtUpPublishedAfterSecondFault => Volatile.Read(ref _caughtUpAfterSecondFault) != 0;
        public int Productions => Volatile.Read(ref _productions);
        public int ProductionsAtSecondFault => Volatile.Read(ref _productionsAtSecondFault);

        public string[] Violations
        {
            get
            {
                lock (_violations)
                {
                    return _violations.ToArray();
                }
            }
        }

        public static async Task<Scenario> CreateAsync(char[] drives, string? faultDuringFirstReconciliation,
            bool faultAfterStartup = true, string? faultOnRecoveredCaughtUp = null)
        {
            var scenario = new Scenario();
            scenario._files.AddMarker("repo");
            var index = await scenario._files.OpenMftIndexAsync(scenario.Scripted, drives);
            scenario._files.BeforeProduceAsync = (_, token) =>
                (Interlocked.Increment(ref scenario._productions) == 1 ? scenario._firstRescan : scenario._heldRescan)
                .Task.WaitAsync(token);
            // The startup fault releases the first rescan itself once it has seen the drive recovering.
            if (!faultAfterStartup)
            {
                scenario._firstRescan.SetResult();
            }

            scenario.Owner = IndexVolumeChangeSourceTests.Owner(index, drives.Select(drive => drive.ToString()).ToArray());
            var owner = scenario.Owner;
            scenario.Source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner))
            {
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
            {
                return Task.CompletedTask;
            }

            Scripted.WatchFor(drive).FailDrive(new IOException($"{drive} failed after its first catch-up"));
            return ReleaseFirstRescanWhenRecoveringAsync(drive);
        }

        async Task ReleaseFirstRescanWhenRecoveringAsync(char drive)
        {
            await WaitUntilAsync(() => StateOf(drive) == WatchState.Recovering);
            _firstRescan.SetResult();
        }

        // Runs inside the source's delivery gate or its readiness publication, so it waits on
        // MFTLib's own state and never on the source's fault handler.
        void InjectSecondFault(char drive, string kind)
        {
            if (Interlocked.Exchange(ref _secondFault, 1) != 0)
            {
                return;
            }

            Volatile.Write(ref _productionsAtSecondFault, Volatile.Read(ref _productions));
            switch (kind)
            {
                case "drive":
                    Scripted.WatchFor(drive).FailDrive(new IOException($"{drive} failed again"));
                    break;
                case "apply":
                    Scripted.WatchFor(drive).FailApply(new IOException($"{drive} apply failed"));
                    break;
                default:
                    Scripted.WatchFor(drive).LoseChannel(new IOException($"{drive} channel lost"));
                    break;
            }
            var settled = kind == "channel" ? WatchState.Faulted : WatchState.Recovering;
            Assert.That(SpinWait.SpinUntil(() => StateOf(drive) == settled, TestTimeouts.HangGuard), Is.True);
            _secondFaultInjected.TrySetResult();
        }

        void OnReadinessChanged(VolumeReadinessSnapshot snapshot, string? faultOnRecoveredCaughtUp)
        {
            if (!snapshot.TryGet("X", out var status) || status.State != VolumeWatchState.CaughtUp)
            {
                return;
            }

            var state = StateOf('X');
            if (state != WatchState.CaughtUp)
            {
                lock (_violations)
                {
                    _violations.Add($"CaughtUp published while MFTLib read {state}");
                }
            }
            if (Volatile.Read(ref _secondFault) != 0)
            {
                Volatile.Write(ref _caughtUpAfterSecondFault, 1);
            }
            else if (faultOnRecoveredCaughtUp is not null && Volatile.Read(ref _productions) >= 1)
            {
                InjectSecondFault('X', faultOnRecoveredCaughtUp);
            }
        }

        public void ReleaseHeldRescan() => _heldRescan.TrySetResult();

        public WatchState? StateOf(char drive) =>
            Owner.Index.Drives.FirstOrDefault(status => status.Drive == drive)?.Watch.State;

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
