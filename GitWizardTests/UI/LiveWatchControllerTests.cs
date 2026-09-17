using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using GitWizardTests.Watch;
using GitWizardUI.Services;
using MFTLib.Index;

namespace GitWizardTests.UI;

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
        var controller = new LiveWatchController(() =>
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
            Assert.That(controller.ScanErrors, Does.ContainKey("X").WithValue("watch failed"));
            Assert.That(errorUpdates.Last(), Does.ContainKey("X").WithValue("watch failed"));
            source.Publish(new VolumeChangeBatch("Y",
                [new VolumeChangeEntry(@"Y:\repo\file", VolumeEntryKind.Modified)]));
            Assert.That((await changed.Task.WaitAsync(cancellation.Token)).RepoRoot, Is.EqualTo(@"Y:\repo"));
            Assert.That(creations, Is.EqualTo(1));
            Assert.That(stopped, Is.Empty);
            Assert.That(running.IsCompleted, Is.False);
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
        Assert.That(completedSource.DiagnosticSubscribers, Is.Zero);
        Assert.That(WatchSubscriptionProbe.ScanErrorsSubscribers(service), Is.Zero,
            "The controller must detach from the service after shutdown.");
    }

    static VolumeChangeBatch Batch(string path, VolumeEntryKind kind) =>
        new("C", new[] { new VolumeChangeEntry(path, kind) });

    [Test]
    public async Task Start_ForwardsEventsInOrder()
    {
        var source = new FakeVolumeChangeSource(
            new[]
            {
                Batch(@"C:\repo\a\one.txt", VolumeEntryKind.Modified),
                Batch(@"C:\repo\b\newrepo\.git", VolumeEntryKind.Created),
            });

        var events = new List<RepositoryChangeEvent>();
        var controller = new LiveWatchController(
            () => source,
            trackedRoots: new[] { @"C:\repo\a" },
            searchRoots: new[] { @"C:\repo\b" },
            onEvent: events.Add,
            onStopped: _ => { },
            isElevated: () => false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = controller.StartAsync(cts.Token);

        await WaitUntilAsync(() => events.Count >= 2);
        await controller.StopAsync();
        await startTask;

        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events[0], Is.EqualTo(
            new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Changed)));
        Assert.That(events[1], Is.EqualTo(
            new RepositoryChangeEvent(@"C:\repo\b\newrepo", RepositoryChangeKind.Created)));
    }

    [Test]
    public async Task SourceDies_NotElevated_CallsOnStoppedOnceWithNoRespawn()
    {
        var source = new HangingKillableSource();
        var sourceFactoryCallCount = 0;
        Func<IVolumeChangeSource> factory = () =>
        {
            sourceFactoryCallCount++;
            return source;
        };

        var stoppedReasons = new List<string>();
        var controller = new LiveWatchController(
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

        Assert.That(sourceFactoryCallCount, Is.EqualTo(1));
        Assert.That(stoppedReasons, Is.EqualTo(new[] { "volume unmounted" }));
    }

    [Test]
    public async Task SourceDies_Elevated_RespawnsWithoutCallingOnStopped()
    {
        var firstSource = new HangingKillableSource();
        var secondSource = new FakeVolumeChangeSource(Array.Empty<VolumeChangeBatch>());
        var sources = new Queue<IVolumeChangeSource>(new IVolumeChangeSource[] { firstSource, secondSource });
        var sourceFactoryCallCount = 0;
        Func<IVolumeChangeSource> factory = () =>
        {
            sourceFactoryCallCount++;
            return sources.Dequeue();
        };

        var stoppedReasons = new List<string>();
        var controller = new LiveWatchController(
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

        Assert.That(sourceFactoryCallCount, Is.EqualTo(2));
        Assert.That(stoppedReasons, Is.Empty);
        Assert.That(firstSource.DisposeCount, Is.EqualTo(1),
            "the dead source must be disposed before the elevated respawn");
    }

    [Test]
    public async Task SourceDies_Elevated_ExceedsMaxRespawns_CallsOnStopped()
    {
        var sources = new Queue<HangingKillableSource>(
            Enumerable.Range(0, 5).Select(_ => new HangingKillableSource()));
        var createdSources = new List<HangingKillableSource>();
        Func<IVolumeChangeSource> factory = () =>
        {
            var next = sources.Dequeue();
            createdSources.Add(next);
            return next;
        };

        var stoppedReasons = new List<string>();
        var controller = new LiveWatchController(
            factory,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            onEvent: _ => { },
            onStopped: stoppedReasons.Add,
            isElevated: () => true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var startTask = controller.StartAsync(cts.Token);

        // Kill 1st, 2nd, 3rd, 4th sources as they arm. MaxConsecutiveRespawns is 3,
        // so the 4th death must not respawn a 5th source and must call onStopped.
        for (var i = 0; i < 4; i++)
        {
            var iteration = i;
            await WaitUntilAsync(() => createdSources.Count > iteration && createdSources[iteration].IsArmed);
            createdSources[iteration].KillSource($"journal invalidated #{iteration + 1}");
        }

        await WaitUntilAsync(() => stoppedReasons.Count >= 1);
        await startTask;

        Assert.Multiple(() =>
        {
            Assert.That(createdSources, Has.Count.EqualTo(4));
            Assert.That(stoppedReasons, Is.EqualTo(new[] { "journal invalidated #4" }));
            Assert.That(createdSources.Select(source => source.DisposeCount),
                Is.EqualTo(new[] { 1, 1, 1, 1 }));
        });
    }

    [Test]
    public async Task StopAsync_DoesNotCallOnStoppedOrRespawn()
    {
        var source = new HangingKillableSource();
        var sourceFactoryCallCount = 0;
        Func<IVolumeChangeSource> factory = () =>
        {
            sourceFactoryCallCount++;
            return source;
        };

        var stoppedReasons = new List<string>();
        var controller = new LiveWatchController(
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

        Assert.That(stoppedReasons, Is.Empty);
        Assert.That(sourceFactoryCallCount, Is.EqualTo(1));
        Assert.That(source.DisposeCount, Is.EqualTo(1));
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
        var secondSource = new HangingKillableSource();
        var sources = new Queue<IVolumeChangeSource>(new IVolumeChangeSource[] { firstSource, secondSource });
        var sourceFactoryCallCount = 0;
        Func<IVolumeChangeSource> factory = () =>
        {
            sourceFactoryCallCount++;
            return sources.Dequeue();
        };

        var stoppedReasons = new List<string>();
        var controller = new LiveWatchController(
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

        Assert.That(sourceFactoryCallCount, Is.EqualTo(1),
            "no new source may be created after a stop that raced an elevated respawn");
        Assert.That(stoppedReasons, Is.Empty);
    }

    // Minor: StopAsync force-disposes the source without awaiting the in-flight run to unwind, so
    // it can stop a source whose WatchAsync ignores its CancellationToken and only surfaces via
    // DisposeAsync. Without that force-dispose this would hang past the 5s guard.
    [Test]
    public async Task StopAsync_UnblocksSourceThatIgnoresCancellation()
    {
        var source = new DisposeToUnblockSource();
        var controller = new LiveWatchController(
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
        var controller = new LiveWatchController(
            () => new HangingKillableSource(),
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
        var source = new FakeVolumeChangeSource(Array.Empty<VolumeChangeBatch>(), errors);

        var controller = new LiveWatchController(
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
            CacheDirectory = files.BlockDirectory,
            NoCache = true,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = (_, _) => throw new IOException("cannot scan X")
        }, CancellationToken.None);
        var owner = new MftIndexSession(failedIndex, null, ["X"], [], new Dictionary<string, string>());
        var stoppedReasons = new List<string>();
        var controller = new LiveWatchController(
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
        var source = new HangingKillableSource();
        var samples = new List<VolumeScanProgress>();
        var controller = new LiveWatchController(
            () => source,
            trackedRoots: new[] { @"C:\repo" },
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

        Assert.That(samples, Is.EqualTo(new[] { sample }));
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    [Test]
    public async Task Start_TempCreatedEvent_DroppedByDefault()
    {
        var source = new FakeVolumeChangeSource(
            new[] { Batch(@"C:\Users\gitea-runner\AppData\Local\Temp\pytest-1\repo\.git", VolumeEntryKind.Created) });

        var events = new List<RepositoryChangeEvent>();
        var controller = new LiveWatchController(
            () => source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: new[] { @"C:\" },
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
        var source = new FakeVolumeChangeSource(
            new[] { Batch(@"C:\Users\gitea-runner\AppData\Local\Temp\pytest-1\repo\.git", VolumeEntryKind.Created) });

        var events = new List<RepositoryChangeEvent>();
        var controller = new LiveWatchController(
            () => source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: new[] { @"C:\" },
            onEvent: events.Add,
            onStopped: _ => { },
            new LiveWatchOptions
            {
                IsElevated = () => false,
                IncludeTempDirectories = true
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await controller.StartAsync(cts.Token);

        Assert.That(events, Is.EqualTo(new[]
        {
            new RepositoryChangeEvent(@"C:\Users\gitea-runner\AppData\Local\Temp\pytest-1\repo", RepositoryChangeKind.Created),
        }));
    }

    [Test]
    public async Task Start_IgnoredPathCreatedEvent_DroppedEvenWhenIncludeTempDirectoriesTrue()
    {
        const string ignoredTempSubtree = @"C:\Users\gitea-runner\AppData\Local\Temp\ci-runner";
        var source = new FakeVolumeChangeSource(
            new[] { Batch(Path.Combine(ignoredTempSubtree, "repo", ".git"), VolumeEntryKind.Created) });

        var events = new List<RepositoryChangeEvent>();
        var controller = new LiveWatchController(
            () => source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: new[] { @"C:\" },
            onEvent: events.Add,
            onStopped: _ => { },
            new LiveWatchOptions
            {
                IsElevated = () => false,
                IncludeTempDirectories = true,
                IgnoredPaths = new[] { ignoredTempSubtree },
            });

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
            var source = new FakeVolumeChangeSource(new[]
            {
                new VolumeChangeBatch(volume, new[]
                {
                    new VolumeChangeEntry(ignoredRepo, VolumeEntryKind.MovedIn),
                    new VolumeChangeEntry(visibleRepo, VolumeEntryKind.MovedIn),
                })
            });
            var events = new List<RepositoryChangeEvent>();
            var controller = new LiveWatchController(
                () => source,
                trackedRoots: Array.Empty<string>(),
                searchRoots: new[] { searchRoot },
                onEvent: events.Add,
                onStopped: _ => { },
                new LiveWatchOptions
                {
                    IsElevated = () => false,
                    IgnoredPaths = new[] { ignoredRoot }
                });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await controller.StartAsync(cts.Token);

            Assert.That(events, Is.EqualTo(new[]
            {
                new RepositoryChangeEvent(visibleRepo, RepositoryChangeKind.Created),
            }));
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }
}


// A source whose WatchAsync hangs until cancelled or killed, and that records whether it was
// armed/disposed - used to deterministically observe LiveWatchController's respawn/stop wiring
// without racing a scripted batch against a kill signal.
internal sealed class HangingKillableSource : IVolumeChangeSource, IVolumeScanProgress
{
    readonly Lock _gate = new();
    Action<string>? _sourceDied;
    string? _deathReason;

    public event Action<string>? SourceDied
    {
        add
        {
            lock (_gate)
            {
                _sourceDied += value;
                if (_deathReason is not null)
                    value?.Invoke(_deathReason);
            }
        }
        remove
        {
            lock (_gate)
                _sourceDied -= value;
        }
    }

    public event Action<VolumeScanProgress>? ScanProgress;

    public bool IsArmed { get; private set; }
    public int DisposeCount { get; private set; }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        IsArmed = true;
        return Task.FromResult(new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public void KillSource(string reason)
    {
        lock (_gate)
        {
            _deathReason = reason;
            _sourceDied?.Invoke(reason);
        }
    }

    public void ReportScanProgress(VolumeScanProgress sample) => ScanProgress?.Invoke(sample);

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

// Like HangingKillableSource, but its DisposeAsync parks until ReleaseDispose() is called - giving
// a test a deterministic barrier inside the respawn path's dispose-before-re-check window.
internal sealed class GatedDisposeKillableSource : IVolumeChangeSource
{
    readonly TaskCompletionSource _releaseDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Lock _gate = new();
    Action<string>? _sourceDied;
    string? _deathReason;

    public event Action<string>? SourceDied
    {
        add
        {
            lock (_gate)
            {
                _sourceDied += value;
                if (_deathReason is not null)
                    value?.Invoke(_deathReason);
            }
        }
        remove
        {
            lock (_gate)
                _sourceDied -= value;
        }
    }

    public bool IsArmed { get; private set; }
    public bool DisposeStarted { get; private set; }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        IsArmed = true;
        return Task.FromResult(new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public void KillSource(string reason)
    {
        lock (_gate)
        {
            _deathReason = reason;
            _sourceDied?.Invoke(reason);
        }
    }

    public void ReleaseDispose() => _releaseDispose.TrySetResult();

    public async ValueTask DisposeAsync()
    {
        DisposeStarted = true;
        await _releaseDispose.Task.ConfigureAwait(false);
    }
}

// A source whose WatchAsync deliberately ignores its CancellationToken and only unblocks when the
// source itself is disposed - used to prove StopAsync's force-dispose path can stop a source that
// never honours cancellation.
internal sealed class DisposeToUnblockSource : IVolumeChangeSource
{
    readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsArmed { get; private set; }
    public int DisposeCount { get; private set; }

    // Never dies on its own; a plain field-like event that is never invoked trips CS0067.
    event Action<string>? IVolumeChangeSource.SourceDied
    {
        add { }
        remove { }
    }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        IsArmed = true;
        return Task.FromResult(new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await _disposed.Task.ConfigureAwait(false);
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        _disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
