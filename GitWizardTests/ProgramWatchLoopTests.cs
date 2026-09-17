using GitWizard;
using GitWizard.CLI;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using GitWizardTests.Watch;
using MFTLib.Index;

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
        source.ArmWarnings.Add(new JournalWarning("C", 12345, 1024 * 1024 * 30, 1024 * 1024 * 60, 1024 * 1024 * 10));
        var service = new RepositoryWatchService(source, [@"C:\repo"], [], TimeSpan.Zero);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = Program.RunWatchLoopAsync(service, line =>
        {
            lines.Enqueue(line);
            if (line.Contains("USN journal maximum size"))
                changed.TrySetResult();
        }, _ => { }, cancellation.Token);

        await changed.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        try { await running; } catch (OperationCanceledException) { }

        Assert.That(lines, Has.Some.Contains("warning on C: USN journal maximum size (30 MB) is below the recommended 60 MB."));
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
            Assert.That(lines, Is.EqualTo(new[] { "scan error on X: watch failed" }));
            source.FailDrive("x", "watch failed");
            source.FailDrive("X", "watch failed again");
            source.Publish(new VolumeChangeBatch("Y",
                [new VolumeChangeEntry(@"Y:\repo\file", VolumeEntryKind.Modified)]));
            await changed.Task.WaitAsync(cancellation.Token);
            Assert.That(running.IsCompleted, Is.False);
            Assert.That(lines, Is.EqualTo(new[]
            {
                "scan error on X: watch failed", "scan error on X: watch failed again", @"changed: Y:\repo"
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
        var source = new FakeVolumeChangeSource(Array.Empty<VolumeChangeBatch>());
        var service = new RepositoryWatchService(
            source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopped = await Program.RunWatchLoopAsync(service, lines.Add, NoExit, cts.Token);

        Assert.That(stopped, Is.False);
        Assert.That(lines, Is.Empty);
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
            MftProducer = (_, _) => throw new IOException("cannot scan X")
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
            Assert.That(lines, Is.EqualTo(new[] { $"created: {repoPath}" }));
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
