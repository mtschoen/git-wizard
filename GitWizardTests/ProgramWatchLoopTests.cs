using GitWizard;
using GitWizard.CLI;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using GitWizardTests.Watch;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests;

// Exercises Program.RunWatchLoopAsync - the testable core of the --watch CLI command - through
// a fake IVolumeChangeSource-backed RepositoryWatchService and a capturing sink, so the printed
// line format for each event kind, scan errors, and broker death can be asserted without a real
// elevated broker or Environment.Exit.
// An IVolumeChangeSource whose ArmAndCatchUpAsync throws InvalidOperationException, standing in
// for MftIndexSession opening's UAC-decline / launch-failure throw that
// IndexVolumeChangeSource surfaces from the first WatchAsync-arming MoveNextAsync.
internal sealed class ThrowingArmVolumeChangeSource : IVolumeChangeSource
{
    readonly string _message;
    public event Action<string>? SourceDied;

    public ThrowingArmVolumeChangeSource(string message) => _message = message;

    public Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct) =>
        throw new InvalidOperationException(_message);

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public void RaiseSourceDied(string reason) => SourceDied?.Invoke(reason);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[TestFixture]
public class ProgramWatchLoopTests
{

    [Test]
    public async Task JournalWarning_PrintsToConsole()
    {
        await using var source = new DiagnosticVolumeChangeSource();
        var loss = FakeJournalSettingsSource.TrimmedLoss('C', 30L * 1024 * 1024, 60L * 1024 * 1024, 10L * 1024 * 1024);
        source.ArmWarnings.Add(new JournalWarning { DriveLetter = "C", VolumeSerial = 12345, Loss = loss });
        var service = new RepositoryWatchService(source, [@"C:\repo"], [], TimeSpan.Zero);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = Program.RunWatchLoopAsync(service, line =>
        {
            lines.Enqueue(line);
            if (line.Contains("Hint on C:"))
                changed.TrySetResult();
        }, _ => { }, cancellation.Token);

        await changed.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        try { await running; } catch (OperationCanceledException) { }

        Assert.That(lines, Has.Some.Contains("Hint on C:"));
        Assert.That(lines, Has.Some.Contains("60 MB"));
    }

    [Test]
    public async Task DriveFailurePrintsWhileOtherDriveContinues()
    {
        await using var source = new DiagnosticVolumeChangeSource();
        var service = new RepositoryWatchService(source, [@"X:\repo", @"Y:\repo"], [], TimeSpan.Zero);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = Program.RunWatchLoopAsync(service, line =>
        {
            lines.Enqueue(line);
            if (line.StartsWith("changed:", StringComparison.Ordinal))
                changed.TrySetResult();
        }, NoExit, cancellation.Token);
        await source.Started;
        try
        {
            Assert.That(WatchSubscriptionProbe.ScanErrorsSubscribers(service), Is.EqualTo(1));
            source.FailDrive("X", "watch failed");
            Assert.That(lines, Is.EqualTo(new[]
            {
                "ready: mode=pump caught-up=X,Y excluded=-",
                "scan error on X: watch failed"
            }));
            source.FailDrive("x", "watch failed");
            source.FailDrive("X", "watch failed again");
            source.Publish(new VolumeChangeBatch("Y",
                [new VolumeChangeEntry(@"Y:\repo\file", VolumeEntryKind.Modified)]));
            await changed.Task.WaitAsync(cancellation.Token);
            Assert.That(running.IsCompleted, Is.False);
            Assert.That(lines, Is.EqualTo(new[]
            {
                "ready: mode=pump caught-up=X,Y excluded=-",
                "scan error on X: watch failed",
                "scan error on X: watch failed again",
                @"changed: Y:\repo"
            }));
        }
        finally
        {
            source.Complete();
            await running;
        }
        Assert.That(source.DiagnosticSubscribers, Is.Zero);
        Assert.That(WatchSubscriptionProbe.ScanErrorsSubscribers(service), Is.Zero,
            "The CLI must detach from the service after shutdown.");
    }

    [Test]
    public async Task RecoveringDrivePrintsNoErrorWhileOtherDriveStaysLive_AndReadsCaughtUpAgainAfterwards()
    {
        await using var source = new ReadinessVolumeChangeSource();
        var service = new RepositoryWatchService(source, [@"X:\repo", @"Y:\repo"], [], TimeSpan.Zero);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = Program.RunWatchLoopAsync(service, line =>
        {
            lines.Enqueue(line);
            if (line.EndsWith(@"changed: Y:\repo", StringComparison.Ordinal))
                changed.TrySetResult();
        }, NoExit, cancellation.Token, verbose: true, new FixedTimeProvider());
        await source.Started;
        try
        {
            source.SetVolume(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Warm));
            source.SetVolume(VolumeWatchStatus.CaughtUp("Y", VolumeBlockOrigin.Warm));
            // MFTLib recovers the faulted drive: it reads as catching up, then caught up again.
            source.SetVolume(VolumeWatchStatus.CatchingUp("X", VolumeBlockOrigin.Cold));
            source.Publish(new VolumeChangeBatch("Y",
                [new VolumeChangeEntry(@"Y:\repo\file", VolumeEntryKind.Modified)]));
            await changed.Task.WaitAsync(cancellation.Token);
            Assert.That(running.IsCompleted, Is.False);
            source.SetVolume(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Cold));
        }
        finally
        {
            source.Complete();
            await running;
        }

        const string stamp = "2026-10-01T00:00:00.000Z ";
        Assert.That(lines.Where(line => line.StartsWith(stamp + "drive:", StringComparison.Ordinal)), Is.EqualTo(new[]
        {
            stamp + "drive: X state=caught-up origin=warm",
            stamp + "drive: Y state=caught-up origin=warm",
            stamp + "drive: X state=catching-up origin=cold",
            stamp + "drive: X state=caught-up origin=cold"
        }));
        Assert.That(lines, Has.None.Contain("error").IgnoreCase.Or.Contain("Hint on"),
            "A drive MFTLib recovers is not an error and leaves no error line behind.");
        Assert.That(lines, Does.Contain(stamp + @"changed: Y:\repo"));
    }

    [Test]
    public async Task RunWatchLoopAsync_PrintsScanErrorsThenChangedCreatedDeletedLines()
    {
        var errors = new Dictionary<string, string> { ["D"] = "drive not ready" };
        var batch = new VolumeChangeBatch("C", new[]
        {
            new VolumeChangeEntry(@"C:\repo\a\file.txt", VolumeEntryKind.Modified),
            new VolumeChangeEntry(@"C:\repo\b\newrepo\.git", VolumeEntryKind.Created),
            new VolumeChangeEntry(@"C:\repo\gone\.git", VolumeEntryKind.Deleted),
        });
        var source = new FakeVolumeChangeSource(new[] { batch }, errors);
        var service = new RepositoryWatchService(
            source,
            trackedRoots: new[] { @"C:\repo\a", @"C:\repo\gone" },
            searchRoots: new[] { @"C:\repo\b" },
            debounce: TimeSpan.FromMilliseconds(10));

        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopped = await Program.RunWatchLoopAsync(service, lines.Add, NoExit, cts.Token);

        Assert.That(stopped, Is.False);
        Assert.That(lines, Is.EqualTo(new[]
        {
            "scan error on D: drive not ready",
            "ready: mode=pump caught-up=C excluded=-",
            @"changed: C:\repo\a",
            @"created: C:\repo\b\newrepo",
            @"deleted: C:\repo\gone",
        }));
    }

    // A no-op exit for the tests that never hit the terminal broker-spawn-failure path.
    static void NoExit(int code) { }

    [Test]
    public async Task RunWatchLoopAsync_NoScanErrors_PrintsNoScanErrorLine()
    {
        // A non-empty batch, not an empty list: an immediate end of stream on the first read is
        // source death, not readiness, so an empty source would never print "ready:" here. The
        // batch names a volume no tracked/search root maps to, so it is silently dropped and
        // never becomes an observable line.
        var source = new FakeVolumeChangeSource([
            new VolumeChangeBatch("Z", [new VolumeChangeEntry(@"Z:\unwatched\path", VolumeEntryKind.Modified)])
        ]);
        var service = new RepositoryWatchService(
            source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopped = await Program.RunWatchLoopAsync(service, lines.Add, NoExit, cts.Token);

        Assert.That(stopped, Is.False);
        Assert.That(lines, Is.EqualTo(new[] { "ready: mode=pump caught-up=- excluded=-" }));
    }

    [Test]
    public async Task RunWatchLoopAsync_SourceDies_PrintsBrokerDiedAndReturnsTrue()
    {
        var source = new HangingVolumeChangeSource();
        var service = new RepositoryWatchService(
            source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loopTask = Program.RunWatchLoopAsync(service, lines.Add, NoExit, cts.Token);

        source.KillSource("volume unmounted");
        var stopped = await loopTask;

        Assert.That(stopped, Is.True);
        Assert.That(lines, Does.Contain("Broker died: volume unmounted"));
    }

    [Test]
    public async Task RunWatchLoopAsync_BrokerSpawnFailure_PrintsMessageAndExitsOne()
    {
        var source = new ThrowingArmVolumeChangeSource("The operation was canceled by the user.");
        var service = new RepositoryWatchService(
            source,
            trackedRoots: new[] { @"C:\repo\a" },
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        var lines = new List<string>();
        var exitCodes = new List<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Program.RunWatchLoopAsync(service, lines.Add, exitCodes.Add, cts.Token);

        Assert.That(lines, Does.Contain(
            "Could not start the journal broker: The operation was canceled by the user."));
        Assert.That(exitCodes, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public async Task RunWatchLoopAsync_MidLoopInvalidOperationException_PrintsBrokerDiedAndReturnsTrue()
    {
        var batch = new VolumeChangeBatch("C", new[]
        {
            new VolumeChangeEntry(@"C:\repo\a\file.txt", VolumeEntryKind.Modified),
        });
        var source = new ThrowingWatchVolumeChangeSource(batch, "pipe broken unexpectedly");
        var service = new RepositoryWatchService(
            source,
            trackedRoots: new[] { @"C:\repo\a" },
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopped = await Program.RunWatchLoopAsync(service, lines.Add, NoExit, cts.Token);

        Assert.That(stopped, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(lines, Does.Contain(@"changed: C:\repo\a"));
            Assert.That(lines, Does.Contain("Broker died: pipe broken unexpectedly"));
        });
    }

    [Test]
    public async Task RunWatchLoopAsync_NoWatchableDrives_PrintsBrokerDiedAndReturnsTrue()
    {
        using var files = new PackedIndexFixture();
        await using var failedIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('X', files.TreePath, 1)],
            CacheDirectory = files.BlockDirectory,
            NoCache = true,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = SyntheticIndexSource.Create(new SyntheticMftProducer(_ => throw new IOException("cannot scan X")))
        }, CancellationToken.None);
        var owner = new MftIndexSession(failedIndex, null, ["X"], [], new Dictionary<string, string>());
        var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        var service = new RepositoryWatchService(
            source,
            trackedRoots: [@"X:\repo"],
            searchRoots: [],
            debounce: TimeSpan.FromMilliseconds(10));

        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopped = await Program.RunWatchLoopAsync(service, lines.Add, NoExit, cts.Token);

        Assert.That(stopped, Is.True);
        Assert.That(lines, Does.Contain("Broker died: No requested drives could be watched."));
    }

    [Test]
    public async Task RunWatchLoopAsync_SoleCacheDeclinedDrive_PrintsHintAndBrokerDied()
    {
        await using var source = new DiagnosticVolumeChangeSource();
        source.ArmErrors["X"] = "cache declined";
        var loss = FakeJournalSettingsSource.TrimmedLoss('X', 32L * 1024 * 1024, 136L * 1024 * 1024);
        source.ArmWarnings.Add(new JournalWarning { DriveLetter = "X", VolumeSerial = 12345, Loss = loss });
        var service = new RepositoryWatchService(source, [@"X:\repo"], [], TimeSpan.Zero);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = Program.RunWatchLoopAsync(service, lines.Enqueue, NoExit, cancellation.Token);
        await source.Started;

        // With no usable drive the real source reports death as soon as the watch starts.
        source.KillSource("No requested drives could be watched: cache declined");
        var stopped = await running;

        Assert.That(stopped, Is.True);
        Assert.That(lines, Has.Some.Contains("scan error on X: cache declined"));
        Assert.That(lines, Has.Some.Contains("Hint on X:").And.Contains("at least 136 MB"));
        Assert.That(lines, Has.Some.Contains("Broker died: No requested drives could be watched: cache declined"));
    }

    [Test]
    public async Task RunWatchLoopAsync_LiveWatchLoss_PrintsUpdatedHintForSameDrive()
    {
        await using var source = new DiagnosticVolumeChangeSource();
        var armLoss = FakeJournalSettingsSource.TrimmedLoss('C', 30L * 1024 * 1024, 60L * 1024 * 1024, 10L * 1024 * 1024);
        source.ArmWarnings.Add(new JournalWarning { DriveLetter = "C", VolumeSerial = 12345, Loss = armLoss });
        var service = new RepositoryWatchService(source, [@"C:\repo"], [], TimeSpan.Zero);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var liveHintPrinted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = Program.RunWatchLoopAsync(service, line =>
        {
            lines.Enqueue(line);
            if (line.Contains("Live updates for this drive stopped", StringComparison.Ordinal))
                liveHintPrinted.TrySetResult();
        }, NoExit, cancellation.Token);
        await source.Started;

        var liveLoss = FakeJournalSettingsSource.TrimmedLoss('C', 30L * 1024 * 1024, 60L * 1024 * 1024,
            10L * 1024 * 1024, JournalCheckpointLossDetection.LiveWatch);
        source.RaiseJournalWarning(new JournalWarning { DriveLetter = "C", VolumeSerial = 12345, Loss = liveLoss });

        // The arm-time hint for the drive was already printed; the live-watch loss rephrases it
        // and must print again rather than be swallowed by the per-drive dedup.
        await liveHintPrinted.Task.WaitAsync(cancellation.Token);
        source.Complete();
        await running;
        Assert.That(lines.Count(line => line.Contains("Hint on C:", StringComparison.Ordinal)), Is.EqualTo(2));
    }

    [Test]
    public async Task RunWatchLoopAsync_MovedInRepository_PrintsCreatedLine()
    {
        var searchRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"watch-moved-in-{Guid.NewGuid():N}");
        var repoPath = Path.Combine(searchRoot, "repo-renamed");
        Directory.CreateDirectory(Path.Combine(repoPath, ".git"));
        try
        {
            var volume = WatchPath.Parse(repoPath).Volume!;
            var batch = new VolumeChangeBatch(volume,
                new[] { new VolumeChangeEntry(repoPath, VolumeEntryKind.MovedIn) });
            var source = new FakeVolumeChangeSource(new[] { batch });
            var service = new RepositoryWatchService(
                source,
                trackedRoots: Array.Empty<string>(),
                searchRoots: new[] { searchRoot },
                debounce: TimeSpan.FromMilliseconds(10));

            var lines = new List<string>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stopped = await Program.RunWatchLoopAsync(service, lines.Add, NoExit, cts.Token);

            Assert.That(stopped, Is.False);
            Assert.That(lines, Is.EqualTo(new[] { $"ready: mode=pump caught-up={volume} excluded=-", $"created: {repoPath}" }));
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }
}

internal sealed class ThrowingWatchVolumeChangeSource : IVolumeChangeSource
{
    readonly VolumeChangeBatch _firstBatch;
    readonly string _faultMessage;
    public event Action<string>? SourceDied;

    public ThrowingWatchVolumeChangeSource(VolumeChangeBatch firstBatch, string faultMessage)
    {
        _firstBatch = firstBatch;
        _faultMessage = faultMessage;
    }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct) =>
        Task.FromResult(new VolumeArmResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return _firstBatch;
        await Task.Delay(50, ct);
        throw new InvalidOperationException(_faultMessage);
    }

    public void RaiseSourceDied(string reason) => SourceDied?.Invoke(reason);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class FixedTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
}
