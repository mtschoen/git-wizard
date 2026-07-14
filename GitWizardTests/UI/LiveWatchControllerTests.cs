using GitWizard.Watch;
using GitWizardTests.Watch;
using GitWizardUI.Services;

namespace GitWizardTests.UI;

[TestFixture]
public class LiveWatchControllerTests
{
    static VolumeChangeBatch Batch(string path, VolumeEntryKind kind) =>
        new("C", new[] { new VolumeChangeEntry(path, kind) });

    [Test]
    public async Task Start_ForwardsEventsInOrder()
    {
        var source = new FakeVolumeChangeSource(
            Array.Empty<VolumeColdRecord>(),
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
        var secondSource = new FakeVolumeChangeSource(
            Array.Empty<VolumeColdRecord>(), Array.Empty<VolumeChangeBatch>());
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
        cts.Cancel();
        await startTask;

        Assert.That(sourceFactoryCallCount, Is.EqualTo(2));
        Assert.That(stoppedReasons, Is.Empty);
        Assert.That(firstSource.DisposeCount, Is.EqualTo(1),
            "the dead source must be disposed before the elevated respawn");
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
        var source = new FakeVolumeChangeSource(
            Array.Empty<VolumeColdRecord>(), Array.Empty<VolumeChangeBatch>(), errors);

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

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }
}

// A source whose WatchAsync hangs until cancelled or killed, and that records whether it was
// armed/disposed - used to deterministically observe LiveWatchController's respawn/stop wiring
// without racing a scripted batch against a kill signal.
internal sealed class HangingKillableSource : IVolumeChangeSource
{
    public event Action<string>? SourceDied;

    public bool IsArmed { get; private set; }
    public int DisposeCount { get; private set; }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        IsArmed = true;
        return Task.FromResult(new VolumeArmResult(
            Array.Empty<VolumeColdRecord>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public void KillSource(string reason) => SourceDied?.Invoke(reason);

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

    public event Action<string>? SourceDied;

    public bool IsArmed { get; private set; }
    public bool DisposeStarted { get; private set; }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        IsArmed = true;
        return Task.FromResult(new VolumeArmResult(
            Array.Empty<VolumeColdRecord>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public void KillSource(string reason) => SourceDied?.Invoke(reason);
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
            Array.Empty<VolumeColdRecord>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
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
