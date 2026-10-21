using System.Runtime.Versioning;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using static GitWizardTests.Watch.Linux.FanotifyEventBuffers;

namespace GitWizardTests.Watch.Linux;

// Fanotify source tests are split by concern because the combined fixture exceeds 500 lines.
[TestFixture]
[SupportedOSPlatform("linux")]
public class FanotifyVolumeChangeSourceLifecycleTests
{
    static readonly string ExistingDirectoryPath = TempDirectory.SystemTempPath;

    [Test]
    public async Task WatchAsync_SignalsPumpStartedOnceTheReadLoopIsActuallyRunning()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(9, 0)
        };
        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        Assert.That(source.PumpStarted.IsCompleted, Is.False,
            "Arming alone must not signal the pump; WatchAsync has not been called yet.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = source.WatchAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNext = enumerator.MoveNextAsync().AsTask();

        // MoveNextAsync only queues PumpEventsAsync onto a pool thread (Task.Run); that queueing
        // alone is not the same as the pump having actually begun reading. PumpStarted must still
        // complete promptly once the queued work actually runs, with no batch required to prove it.
        await source.PumpStarted.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        try
        {
            await moveNext;
        }
        catch (OperationCanceledException)
        {
            // Expected: cancellation unwinds the still-pending read.
        }
    }

    [Test]
    public async Task WatchAsync_PollFails_RaisesSourceDied()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            PollResult = -1
        };

        var source = new FanotifyVolumeChangeSource(native);
        string? diedReason = null;
        source.SourceDied += reason => diedReason = reason;

        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
            batches.Add(batch);

        Assert.Multiple(() =>
        {
            Assert.That(batches, Is.Empty);
            Assert.That(diedReason, Does.Contain("poll failed"));
        });
    }

    [Test]
    public async Task WatchAsync_ReadReturnsZero_RaisesSourceDied()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            ReadReturnsZero = true
        };

        var source = new FanotifyVolumeChangeSource(native);
        string? diedReason = null;
        source.SourceDied += reason => diedReason = reason;

        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
            batches.Add(batch);

        Assert.Multiple(() =>
        {
            Assert.That(batches, Is.Empty);
            Assert.That(diedReason, Does.Contain("no bytes"));
        });
    }

    [Test]
    public async Task WatchAsync_ReadThrowsException_RaisesSourceDied()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            ReadThrowsException = true
        };

        var source = new FanotifyVolumeChangeSource(native);
        string? diedReason = null;
        source.SourceDied += reason => diedReason = reason;

        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
            batches.Add(batch);

        Assert.Multiple(() =>
        {
            Assert.That(batches, Is.Empty);
            Assert.That(diedReason, Does.Contain("pump faulted"));
        });
    }

    [Test]
    public async Task DisposeAsync_DisposesDescriptorAndAllowsCleanExit()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(77, 0)
        };

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        await source.DisposeAsync();

        Assert.That(native.ClosedDescriptors, Does.Contain(77));
    }

    [Test]
    public async Task WatchAsync_QueueOverflowEvent_RaisesSourceDiedAndTerminates()
    {
        var overflowBuffer = CreateSyntheticOverflowBuffer();
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };
        native.ReadQueue.Enqueue(overflowBuffer);

        var source = new FanotifyVolumeChangeSource(native);
        string? diedReason = null;
        source.SourceDied += reason => diedReason = reason;

        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
            batches.Add(batch);

        Assert.Multiple(() =>
        {
            Assert.That(batches, Is.Empty);
            Assert.That(diedReason, Does.Contain("overflow"));
        });
    }

    [Test]
    public async Task WatchAsync_CancelledDuringWatch_TerminatesPumpCleanlyWithoutFaulting()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var enumerator = source.WatchAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        await cts.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());

        await source.DisposeAsync();
        Assert.That(native.ClosedDescriptors, Does.Contain(7));
    }

    [Test]
    public async Task WatchAsync_DisposeAsyncConcurrentWithEnumeration_DoesNotThrowObjectDisposedException()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var enumerator = source.WatchAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        // Simulate LiveWatchController.StopAsync: cancel and immediately force-dispose the source concurrently.
        await cts.CancelAsync();
        await source.DisposeAsync();

        // MoveNextAsync should either complete with false or throw OperationCanceledException,
        // but must never throw ObjectDisposedException.
        try
        {
            await moveNextTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }

        await enumerator.DisposeAsync();
        Assert.That(native.ClosedDescriptors, Does.Contain(7));
    }

}
