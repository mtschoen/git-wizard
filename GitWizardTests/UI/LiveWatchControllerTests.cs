using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using GitWizardUI.Services;
using MFTLib.Index;
using MFTLibTestExtensions;
using static GitWizardTests.TestSupport.AsyncCondition;

namespace GitWizardTests.UI;

// Controller tests exercise the desktop watch lifecycle, respawn harness, and background loop synchronization.
[TestFixture]
public class LiveWatchControllerTests
{
    [Test]
    public async Task DriveFailureDoesNotRespawnController()
    {
        var sourceReady = new TaskCompletionSource<DiagnosticVolumeChangeSource>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var creations = 0;
        var stopped = new List<string>();
        var changed = new TaskCompletionSource<RepositoryChangeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = NewController(() =>
        {
            creations++;
            var source = new DiagnosticVolumeChangeSource();
            sourceReady.TrySetResult(source);
            return source;
        },
            [@"X:\repo", @"Y:\repo"], [], change => changed.TrySetResult(change), stopped.Add, () => true);
        var errorUpdates = new List<IReadOnlyDictionary<string, string>>();
        controller.ScanErrorsChanged += () => errorUpdates.Add(controller.ScanErrors);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = controller.StartAsync(cancellation.Token);
        RepositoryWatchService service;
        try
        {
            var source = await sourceReady.Task.WaitAsync(cancellation.Token);
            await source.Started;
            service = WatchSubscriptionProbe.Service(controller);
            Assert.That(WatchSubscriptionProbe.ScanErrorsSubscribers(service), Is.EqualTo(1));
            source.FailDrive("X", "watch failed");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(controller.ScanErrors, Does.ContainKey("X").WithValue("watch failed"));
                Assert.That(errorUpdates[^1], Does.ContainKey("X").WithValue("watch failed"));
            }
            source.Publish(new VolumeChangeBatch("Y",
                [new VolumeChangeEntry(@"Y:\repo\file", VolumeEntryKind.Modified)]));
            using (Assert.EnterMultipleScope())
            {
                Assert.That((await changed.Task.WaitAsync(cancellation.Token)).RepoRoot, Is.EqualTo(@"Y:\repo"));
                Assert.That(creations, Is.EqualTo(1));
                Assert.That(stopped, Is.Empty);
                Assert.That(running.IsCompleted, Is.False);
            }
        }
        finally
        {
            try
            {
                await controller.StopAsync();
            }
            finally
            {
                await running;
            }
        }
        var completedSource = await sourceReady.Task;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completedSource.DiagnosticSubscribers, Is.Zero);
            Assert.That(WatchSubscriptionProbe.ScanErrorsSubscribers(service), Is.Zero,
                "The controller must detach from the service after shutdown.");
        }
    }

    static LiveWatchController NewController(
        Func<IVolumeChangeSource> sourceFactory,
        IReadOnlyList<string> trackedRoots,
        IReadOnlyList<string> searchRoots,
        Action<RepositoryChangeEvent> onEvent,
        Action<string> onStopped,
        Func<bool> isElevated,
        IReadOnlyList<string>? ignoredPaths = null,
        bool includeTempDirectories = false) =>
        new(
            FakeLiveStartup.Factory(sourceFactory, trackedRoots, searchRoots, ignoredPaths, includeTempDirectories),
            onEvent,
            onStopped,
            isElevated);
    [Test]
    public async Task Start_ForwardsEventsInOrder()
    {
        // The search root must exist: startup scope expansion drops search roots that do not.
        var searchRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"live-order-{Guid.NewGuid():N}");
        var trackedRoot = Path.Combine(searchRoot, "a");
        var newRepository = Path.Combine(searchRoot, "newrepo");
        Directory.CreateDirectory(searchRoot);
        try
        {
            var volume = WatchPath.Parse(searchRoot).Volume!;
            var source = new ScriptedVolumeChangeSource(
                [
                    new VolumeChangeBatch(volume, [new VolumeChangeEntry(Path.Combine(trackedRoot, "one.txt"), VolumeEntryKind.Modified)]),
                    new VolumeChangeBatch(volume, [new VolumeChangeEntry(Path.Combine(newRepository, ".git"), VolumeEntryKind.Created)]),
                ]);

            var events = new List<RepositoryChangeEvent>();
            var controller = NewController(
                () => source,
                trackedRoots: [trackedRoot],
                searchRoots: [searchRoot],
                onEvent: events.Add,
                onStopped: _ => { },
                isElevated: () => false);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var startTask = controller.StartAsync(cts.Token);

            await WaitUntilAsync(() => events.Count >= 2);
            await controller.StopAsync();
            await startTask;

            Assert.That(events, Has.Count.EqualTo(2));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(events[0], Is.EqualTo(new RepositoryChangeEvent(trackedRoot, RepositoryChangeKind.Changed)));
                Assert.That(events[1], Is.EqualTo(new RepositoryChangeEvent(newRepository, RepositoryChangeKind.Created)));
            }
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }
    [Test]
    public async Task SourceDies_NotElevated_CallsOnStoppedOnceWithNoRespawn()
    {
        var source = new ScriptedVolumeChangeSource { KeepWatching = true, ReplayDeathToLateSubscribers = true };
        var sourceFactoryCallCount = 0;
        Func<IVolumeChangeSource> factory = () =>
        {
            sourceFactoryCallCount++;
            return source;
        };

        var stoppedReasons = new List<string>();
        var controller = NewController(
            factory,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            onEvent: _ => { },
            onStopped: stoppedReasons.Add,
            isElevated: () => false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = controller.StartAsync(cts.Token);

        await WaitUntilAsync(() => source.IsArmed);
        source.KillSource("volume unmounted");

        await WaitUntilAsync(() => stoppedReasons.Count >= 1);
        await startTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceFactoryCallCount, Is.EqualTo(1));
            Assert.That(stoppedReasons, Is.EqualTo(["volume unmounted"]));
        }
    }

    [Test]
    public async Task SourceDies_Elevated_RespawnsWithoutCallingOnStopped()
    {
        var firstSource = new ScriptedVolumeChangeSource { KeepWatching = true, ReplayDeathToLateSubscribers = true };
        var secondSource = new ScriptedVolumeChangeSource(Array.Empty<VolumeChangeBatch>());
        var sources = new Queue<IVolumeChangeSource>([firstSource, secondSource]);
        var sourceFactoryCallCount = 0;
        var factory = () =>
        {
            sourceFactoryCallCount++;
            return sources.Dequeue();
        };

        var stoppedReasons = new List<string>();
        var controller = NewController(
            factory,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            onEvent: _ => { },
            onStopped: stoppedReasons.Add,
            isElevated: () => true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = controller.StartAsync(cts.Token);

        await WaitUntilAsync(() => firstSource.IsArmed);
        firstSource.KillSource("driver hiccup");

        await WaitUntilAsync(() => sourceFactoryCallCount >= 2);
        await cts.CancelAsync();
        await startTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceFactoryCallCount, Is.EqualTo(2));
            Assert.That(stoppedReasons, Is.Empty);
            Assert.That(firstSource.DisposeCount, Is.EqualTo(1),
                "the dead source must be disposed before the elevated respawn");
        }
    }

    [Test]
    public Task SourceDies_Elevated_ExceedsMaxRespawns_CallsOnStopped() =>
        VerifyRespawnLimitAsync();

    static async Task VerifyRespawnLimitAsync()
    {
        var sources = Enumerable.Range(0, 5)
            .Select(_ => new ScriptedVolumeChangeSource { KeepWatching = true, ReplayDeathToLateSubscribers = true }).ToArray();
        var sourceFactoryCallCount = 0;
        var stoppedReasons = new List<string>();
        var controller = NewController(
            () => sources[sourceFactoryCallCount++],
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            onEvent: _ => { },
            onStopped: stoppedReasons.Add,
            isElevated: () => true);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = controller.StartAsync(cancellation.Token);
        try
        {
            for (var iteration = 0; iteration < 4; iteration++)
            {
                await sources[iteration].Armed.WaitAsync(cancellation.Token);
                sources[iteration].KillSource($"journal invalidated #{iteration + 1}");
            }
            await running.WaitAsync(cancellation.Token);

            // Plain sequential asserts, not Assert.Multiple: this scenario also runs as up
            // to four concurrent workers from RespawnLimit_Stress below, and NUnit's
            // AssertionScope stack backing Assert.Multiple is not safe under concurrent use
            // within a single test (nunit/nunit#4814, fixed upstream in 4.5.0 but not yet
            // picked up here) - it throws "the assertion scope was disposed out of order"
            // instead of reporting the scenario's own failure. Losing multi-assert
            // aggregation only means a failing run reports its first violated assertion
            // rather than every violated one.
            Assert.That(sourceFactoryCallCount, Is.EqualTo(4));
            Assert.That(stoppedReasons, Is.EqualTo(["journal invalidated #4"]));
            Assert.That(sources.Take(4).Select(source => source.DisposeCount),
                Is.EqualTo([1, 1, 1, 1]));
            Assert.That(sources[4].IsArmed, Is.False);
            Assert.That(sources[4].DisposeCount, Is.Zero);
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await controller.StopAsync();
            }
            finally
            {
                await running;
            }
        }
    }

    [Test, Explicit("Runs 200 respawn-limit scenarios with four concurrent workers.")]
    [Category("Stress")]
    public Task RespawnLimit_Stress() =>
        Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var iteration = 0; iteration < 50; iteration++)
            {
                await VerifyRespawnLimitAsync();
            }
        })));

    [Test]
    public async Task StopAsync_DoesNotCallOnStoppedOrRespawn()
    {
        var source = new ScriptedVolumeChangeSource { KeepWatching = true, ReplayDeathToLateSubscribers = true };
        var sourceFactoryCallCount = 0;
        Func<IVolumeChangeSource> factory = () =>
        {
            sourceFactoryCallCount++;
            return source;
        };

        var stoppedReasons = new List<string>();
        var controller = NewController(
            factory,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            onEvent: _ => { },
            onStopped: stoppedReasons.Add,
            isElevated: () => true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = controller.StartAsync(cts.Token);

        await WaitUntilAsync(() => source.IsArmed);
        await controller.StopAsync();
        await startTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stoppedReasons, Is.Empty);
            Assert.That(sourceFactoryCallCount, Is.EqualTo(1));
            Assert.That(source.DisposeCount, Is.EqualTo(1));
        }
    }

    // Critical 2 (TOCTOU): a StopAsync landing while an elevated respawn is mid-flight must win -
    // no new source may be spun up after stop, and StartAsync must complete. The first source's
    // DisposeAsync (which the respawn path awaits *before* re-checking the stop flag) is used as a
    // deterministic barrier: we park in it, request stop, then release - forcing the loop's
    // atomic top-of-iteration re-check to observe the stop before it can call the factory again.
    [Test]
    public async Task StopAsync_DuringElevatedRespawn_CreatesNoNewSourceAndCompletes()
    {
        var firstSource = new GatedDisposeKillableSource();
        var secondSource = new ScriptedVolumeChangeSource { KeepWatching = true, ReplayDeathToLateSubscribers = true };
        var sources = new Queue<IVolumeChangeSource>([firstSource, secondSource]);
        var sourceFactoryCallCount = 0;
        var factory = () =>
        {
            sourceFactoryCallCount++;
            return sources.Dequeue();
        };

        var stoppedReasons = new List<string>();
        var controller = NewController(
            factory,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            onEvent: _ => { },
            onStopped: stoppedReasons.Add,
            isElevated: () => true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = controller.StartAsync(cts.Token);

        await WaitUntilAsync(() => firstSource.IsArmed);
        firstSource.KillSource("driver hiccup");

        await WaitUntilAsync(() => firstSource.DisposeStarted);
        await controller.StopAsync();
        firstSource.ReleaseDispose();

        await startTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceFactoryCallCount, Is.EqualTo(1),
                    "no new source may be created after a stop that raced an elevated respawn");
            Assert.That(stoppedReasons, Is.Empty);
        }
    }

    // Minor: StopAsync force-disposes the source without awaiting the in-flight run to unwind, so
    // it can stop a source whose WatchAsync ignores its CancellationToken and only surfaces via
    // DisposeAsync. Without that force-dispose this would hang past the 5s guard.
    [Test]
    public async Task StopAsync_UnblocksSourceThatIgnoresCancellation()
    {
        var source = new DisposeToUnblockSource();
        var controller = NewController(
            () => source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            onEvent: _ => { },
            onStopped: _ => { },
            isElevated: () => false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = controller.StartAsync(cts.Token);

        await WaitUntilAsync(() => source.IsArmed);
        await controller.StopAsync();
        await startTask;

        Assert.That(source.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public void ScanErrors_BeforeStart_IsEmpty()
    {
        var controller = NewController(
            () => new ScriptedVolumeChangeSource { KeepWatching = true, ReplayDeathToLateSubscribers = true },
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            onEvent: _ => { },
            onStopped: _ => { },
            isElevated: () => false);

        Assert.That(controller.ScanErrors, Is.Empty);
    }

    [Test]
    public async Task ScanErrors_ReadsThroughToUnderlyingService()
    {
        var errors = new Dictionary<string, string> { ["D"] = "drive not ready" };
        var source = new ScriptedVolumeChangeSource(Array.Empty<VolumeChangeBatch>(), errors);

        var controller = NewController(
            () => source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            onEvent: _ => { },
            onStopped: _ => { },
            isElevated: () => false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = controller.StartAsync(cts.Token);

        await WaitUntilAsync(() => controller.ScanErrors.Count > 0);
        await controller.StopAsync();
        await startTask;

        Assert.That(controller.ScanErrors, Does.ContainKey("D").WithValue("drive not ready"));
    }

    [Test]
    public async Task Start_WhenNoDrivesWatchable_CallsOnStopped()
    {
        using var files = new PackedIndexFixture();
        await using var failedIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('X', files.TreePath, 1)],
            CacheMode = CacheMode.Disabled,
            MftSource = SyntheticIndexSource.Create(new SyntheticMftProducer(_ => throw new IOException("cannot scan X")))
        }, CancellationToken.None);
        var owner = new MftIndexSession(failedIndex, null, ["X"], [], new Dictionary<string, string>());
        var stoppedReasons = new List<string>();
        var controller = NewController(
            () => new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner)),
            trackedRoots: [@"X:\repo"],
            searchRoots: [],
            onEvent: _ => { },
            onStopped: stoppedReasons.Add,
            isElevated: () => false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await controller.StartAsync(cts.Token);

        Assert.That(stoppedReasons, Has.Count.EqualTo(1));
        Assert.That(stoppedReasons[0], Is.EqualTo("No requested drives could be watched."));
    }

    [Test]
    public async Task Start_ForwardsScanProgressFromSource()
    {
        var source = new ScriptedVolumeChangeSource { KeepWatching = true, ReplayDeathToLateSubscribers = true };
        var samples = new List<VolumeScanProgress>();
        var controller = NewController(
            () => source,
            trackedRoots: [@"C:\repo"],
            searchRoots: Array.Empty<string>(),
            onEvent: _ => { },
            onStopped: _ => { },
            isElevated: () => false);
        controller.ScanProgress += samples.Add;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = controller.StartAsync(cts.Token);
        await WaitUntilAsync(() => source.IsArmed);
        var sample = new VolumeScanProgress("C", 1, 1, 600_000u, 1_200_000u);
        source.ReportScanProgress(sample);
        await WaitUntilAsync(() => samples.Count >= 1);
        await controller.StopAsync();
        await startTask;

        Assert.That(samples, Is.EqualTo([sample]));
    }



    [Test]
    public async Task Start_TempCreatedEvent_DroppedByDefault()
    {
        var repositoryPath = Path.Combine(TempDirectory.SystemTempPath, "gw-controller", "repo");
        var source = new ScriptedVolumeChangeSource(
            [ new VolumeChangeBatch(WatchPath.Parse(repositoryPath).Volume!,
                [new VolumeChangeEntry(Path.Combine(repositoryPath, ".git"), VolumeEntryKind.Created)]) ]);

        var events = new List<RepositoryChangeEvent>();
        var controller = NewController(
            () => source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: [Path.GetPathRoot(repositoryPath)!],
            onEvent: events.Add,
            onStopped: _ => { },
            isElevated: () => false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await controller.StartAsync(cts.Token);

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task Start_TempCreatedEvent_ForwardedWhenIncluded()
    {
        var repositoryPath = Path.Combine(TempDirectory.SystemTempPath, "gw-controller", "repo");
        var source = new ScriptedVolumeChangeSource(
            [ new VolumeChangeBatch(WatchPath.Parse(repositoryPath).Volume!,
                [new VolumeChangeEntry(Path.Combine(repositoryPath, ".git"), VolumeEntryKind.Created)]) ]);

        var events = new List<RepositoryChangeEvent>();
        var controller = NewController(
            () => source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: [Path.GetPathRoot(repositoryPath)!],
            onEvent: events.Add,
            onStopped: _ => { },
            isElevated: () => false,
            includeTempDirectories: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await controller.StartAsync(cts.Token);

        Assert.That(events, Is.EqualTo([
            new RepositoryChangeEvent(repositoryPath, RepositoryChangeKind.Created),
        ]));
    }

    [Test]
    public async Task Start_IgnoredPathCreatedEvent_DroppedEvenWhenIncludeTempDirectoriesTrue()
    {
        var ignoredTempSubtree = Path.Combine(TempDirectory.SystemTempPath, "gw-controller", "ignored");
        var source = new ScriptedVolumeChangeSource(
            [ new VolumeChangeBatch(WatchPath.Parse(ignoredTempSubtree).Volume!,
                [new VolumeChangeEntry(Path.Combine(ignoredTempSubtree, "repo", ".git"), VolumeEntryKind.Created)]) ]);

        var events = new List<RepositoryChangeEvent>();
        var controller = NewController(
            () => source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: [Path.GetPathRoot(ignoredTempSubtree)!],
            onEvent: events.Add,
            onStopped: _ => { },
            isElevated: () => false,
            ignoredPaths: [ignoredTempSubtree],
            includeTempDirectories: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await controller.StartAsync(cts.Token);

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task Start_ForwardsIgnoredRootsToService()
    {
        var searchRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"live-ignored-{Guid.NewGuid():N}");
        var ignoredRoot = Path.Combine(searchRoot, "ignored");
        var ignoredRepo = Path.Combine(ignoredRoot, "repo");
        var visibleRepo = Path.Combine(searchRoot, "visible-repo");
        Directory.CreateDirectory(Path.Combine(ignoredRepo, ".git"));
        Directory.CreateDirectory(Path.Combine(visibleRepo, ".git"));
        try
        {
            var volume = WatchPath.Parse(searchRoot).Volume!;
            var source = new ScriptedVolumeChangeSource([
                new VolumeChangeBatch(volume,
                [
                    new VolumeChangeEntry(ignoredRepo, VolumeEntryKind.MovedIn),
                    new VolumeChangeEntry(visibleRepo, VolumeEntryKind.MovedIn),
                ])
            ]);
            var events = new List<RepositoryChangeEvent>();
            var controller = NewController(
                () => source,
                trackedRoots: Array.Empty<string>(),
                searchRoots: [searchRoot],
                onEvent: events.Add,
                onStopped: _ => { },
                isElevated: () => false,
                ignoredPaths: [ignoredRoot]);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await controller.StartAsync(cts.Token);

            Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(visibleRepo, RepositoryChangeKind.Created),
            ]));
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }

    [Test]
    public async Task HangingSourcePublishesArmCompletion()
    {
        await using var source = new ScriptedVolumeChangeSource { KeepWatching = true, ReplayDeathToLateSubscribers = true };
        var armed = source.Armed;
        Assert.That(armed.IsCompleted, Is.False);
        await source.ArmAndCatchUpAsync([], CancellationToken.None);
        Assert.That(armed.IsCompletedSuccessfully, Is.True);
        await armed;
        Assert.That(source.IsArmed, Is.True);
    }

    [Test]
    public async Task ForwardsReadyOncePerRunAndDetaches()
    {
        var source = new ReadinessVolumeChangeSource();
        var controller = NewController(() => source, [@"X:\repo"], [], _ => { }, _ => { }, () => false);
        var ready = new List<WatchReadiness>();
        var snapshots = new List<VolumeReadinessSnapshot>();
        controller.Ready += ready.Add;
        controller.VolumeReadinessChanged += snapshots.Add;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = controller.StartAsync(cancellation.Token);
        RepositoryWatchService service;
        try
        {
            await source.Started.WaitAsync(cancellation.Token);
            service = WatchSubscriptionProbe.Service(controller);
            source.SetVolume(VolumeWatchStatus.CatchingUp("X", VolumeBlockOrigin.Cold));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ready, Is.Empty);
                Assert.That(controller.Readiness, Is.Null);
            }
            source.SetVolume(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Cold));
            await WaitUntilAsync(() => ready.Count == 1);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(controller.Readiness, Is.SameAs(ready[0]));
                Assert.That(snapshots[^1].CaughtUpVolumes, Is.EqualTo(["X"]));
            }
        }
        finally
        {
            try { await controller.StopAsync(); } finally { await running; }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.ReadinessSubscribers, Is.Zero, "The service must detach from the source.");
            Assert.That(WatchSubscriptionProbe.ReadySubscribers(service), Is.Zero, "The controller must detach from the service.");
        }
    }

    [Test]
    public async Task ForwardsStartupProgressFromTheStartupFactory()
    {
        var source = new ReadinessVolumeChangeSource();
        source.SetVolume(VolumeWatchStatus.CaughtUp("C", VolumeBlockOrigin.Warm));
        var request = new DiscoveryRequest(new GitWizardConfiguration(), DiscoveryPurpose.Watch, false, false, [@"C:\repo"]);
        var phases = new List<WatchStartupPhase>();
        var controller = new LiveWatchController(
            async (progress, ct) =>
            {
                progress.Report(new WatchStartupProgress(WatchStartupPhase.Opening));
                progress.Report(new WatchStartupProgress(WatchStartupPhase.CatchingUp, null, source.Readiness));
                return await FakeLiveStartup.FromSourceArmedAsync(source, request, ct);
            },
            _ => { }, _ => { }, () => false);
        controller.StartupProgress += report => phases.Add(report.Phase);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = controller.StartAsync(cancellation.Token);
        await WaitUntilAsync(() => controller.Readiness is not null);
        await controller.StopAsync();
        await running;
        Assert.That(phases, Is.EqualTo([WatchStartupPhase.Opening, WatchStartupPhase.CatchingUp]));
    }
}


// A source whose WatchAsync hangs until cancelled or killed, and that records whether it was
// armed/disposed - used to deterministically observe LiveWatchController's respawn/stop wiring
// without racing a scripted batch against a kill signal.
