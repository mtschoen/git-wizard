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
