using System.Globalization;
using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;

namespace GitWizardTests;

[TestFixture]
public class MftIndexSessionTests
{
    [Test]
    public async Task ConnectsOnceAcrossDrives()
    {
        await using var broker = new BlockBrokerFixture();
        await using (var empty = await MftIndexSession.OpenAsync([], broker.ConnectAsync,
            broker.CacheDirectory, CancellationToken.None))
        {
            Assert.That(empty.Drives, Is.Empty);
            Assert.That(broker.ConnectionCount, Is.Zero);
        }
        await using var session = await MftIndexSession.OpenAsync([broker.Drive('X'), broker.Drive('Y')],
            broker.ConnectAsync, broker.CacheDirectory, CancellationToken.None);
        Assert.That(broker.ConnectionCount, Is.EqualTo(1));
        Assert.That(session.Drives, Is.EqualTo(new[] { "X", "Y" }));
        Assert.That(session.Index.Drives.Select(drive => drive.State), Is.All.EqualTo(DriveState.Ready));
        Assert.That(broker.ScanFrames, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task DiscoveryAndWatchShareClient()
    {
        await using var broker = new BlockBrokerFixture();
        var session = await MftIndexSession.OpenAsync([broker.Drive('X')],
            broker.ConnectAsync, broker.CacheDirectory, CancellationToken.None);
        try
        {
            await session.Index.StartWatchingAsync(CancellationToken.None);
            var frame = await broker.WatchStarted.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(frame.DrivesSpec, Does.StartWith("X:"));
            Assert.That(broker.ConnectionCount, Is.EqualTo(1));
            Assert.That(session.IsFaulted, Is.False);
            var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Index.WatchFaulted += _ => fault.TrySetResult();
            await broker.DisconnectAsync();
            await fault.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(session.IsFaulted, Is.True);
        }
        finally
        {
            await session.DisposeAsync();
        }
        Assert.That(session.IsFaulted, Is.True);
        Assert.That(session.IsDisposed, Is.True);
    }

    [Test]
    public async Task DisposeClosesIndexThenClient()
    {
        using var files = new PackedIndexFixture();
        var index = await files.OpenIndexAsync();
        var lifetime = new CountingLifetime(() =>
            Assert.Throws<ObjectDisposedException>(() => index.FindByName(".git")));
        var session = new MftIndexSession(index, lifetime, ["X"], new Dictionary<string, string>());
        var first = session.DisposeAsync().AsTask();
        await lifetime.Started.Task;
        var second = session.DisposeAsync().AsTask();
        Assert.That(session.IsDisposed, Is.True);
        Assert.That(second.IsCompleted, Is.False);
        lifetime.Release.SetResult();
        await Task.WhenAll(first, second);
        await session.DisposeAsync();
        Assert.That(lifetime.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task CancellationClosesClient()
    {
        await using var broker = new BlockBrokerFixture();
        using var cancellation = new CancellationTokenSource();
        var midWriteReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.MidScanAsync = async () =>
        {
            midWriteReached.SetResult();
            await resumeWrite.Task;
        };
        var drive = broker.Drive('X');
        var connectAsync = broker.ConnectAsync;
        var cacheDirectory = broker.CacheDirectory;
        var cancellationToken = cancellation.Token;
        var openTask = MftIndexSession.OpenAsync([drive], connectAsync,
            cacheDirectory, cancellationToken);
        try
        {
            var reached = await Task.WhenAny(midWriteReached.Task, openTask).WaitAsync(TimeSpan.FromSeconds(10));
            if (reached == openTask)
            {
                await using var earlySession = await openTask;
                Assert.Fail("Opening completed before reaching the mid-scan gate.");
            }
        }
        finally
        {
            try
            {
                await cancellation.CancelAsync();
            }
            finally
            {
                resumeWrite.TrySetResult();
            }
        }
        Assert.That(async () => await openTask.WaitAsync(TimeSpan.FromSeconds(10)),
            Throws.InstanceOf<OperationCanceledException>());
        await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(broker.ConnectionCount, Is.EqualTo(1));
        Assert.That(broker.SectionDisposals, Is.EqualTo(1));
        Assert.That(broker.RowsWritten, Is.EqualTo(1));
        Assert.That(broker.ServedToCompletion, Is.False);

        await using var watchBroker = new BlockBrokerFixture();
        using var watchCancellation = new CancellationTokenSource();
        var session = await MftIndexSession.OpenAsync([watchBroker.Drive('X')],
            watchBroker.ConnectAsync, watchBroker.CacheDirectory, CancellationToken.None);
        await session.Index.StartWatchingAsync(watchCancellation.Token);
        await watchBroker.WatchStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var cancelling = watchCancellation.CancelAsync();
        var firstDisposal = session.DisposeAsync().AsTask();
        var secondDisposal = session.DisposeAsync().AsTask();
        await Task.WhenAll(cancelling, firstDisposal, secondDisposal);
        await watchBroker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(session.IsDisposed, Is.True);
        Assert.Throws<ObjectDisposedException>(() => session.Index.FindByName(".git"));
        Assert.That(watchBroker.ConnectionCount, Is.EqualTo(1));
        Assert.That(watchBroker.SectionDisposals, Is.EqualTo(1));
    }

    [Test]
    public async Task CancellationOnScanReceivedClosesClient()
    {
        await using var broker = new BlockBrokerFixture();
        using var cancellation = new CancellationTokenSource();
        broker.ScanReceived = cancellation.Cancel;
        var drive = broker.Drive('X');
        var connectAsync = broker.ConnectAsync;
        var cacheDirectory = broker.CacheDirectory;
        var cancellationToken = cancellation.Token;
        Assert.That(async () =>
            await MftIndexSession.OpenAsync([drive], connectAsync,
                cacheDirectory, cancellationToken), Throws.InstanceOf<OperationCanceledException>());
        await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(broker.ConnectionCount, Is.EqualTo(1));
        Assert.That(broker.SectionDisposals, Is.EqualTo(1));
        Assert.That(broker.RowsWritten, Is.Zero);
        Assert.That(broker.ServedToCompletion, Is.False);
    }

    [Test]
    public async Task OneDriveFailureKeepsOtherDrive()
    {
        await using var broker = new BlockBrokerFixture(scanError: "volume denied") { FailedDrive = "X" };
        await using var session = await MftIndexSession.OpenSelectedVolumesAsync(
            ["w:/", "X:", "x:\\", "Y", @"\\server\share", "C:not-root", ""],
            drive => drive == "W" ? throw new IOException("identity denied") : broker.Drive(drive[0]),
            broker.ConnectAsync, broker.CacheDirectory, CancellationToken.None);
        Assert.That(session.Drives, Is.EqualTo(new[] { "W", "X", "Y" }));
        Assert.That(session.ScanErrors["W"], Does.Contain("identity denied"));
        Assert.That(session.ScanErrors["X"], Does.Contain("volume denied"));
        Assert.That(session.ScanErrors.ContainsKey("Y"), Is.False);
        Assert.That(session.Index.Drives.Single(drive => drive.DriveLetter == 'Y').State, Is.EqualTo(DriveState.Ready));
        Assert.That(broker.ConnectionCount, Is.EqualTo(1));
    }

    [Test]
    public async Task AllDrivesFailDoesNotLeak()
    {
        await using (var broker = new BlockBrokerFixture(scanError: "volume denied"))
        {
            var session = await MftIndexSession.OpenAsync([broker.Drive('X'), broker.Drive('Y')],
                broker.ConnectAsync, broker.CacheDirectory, CancellationToken.None);
            Assert.That(session.ScanErrors.Keys, Is.EquivalentTo(new[] { "X", "Y" }));
            await session.DisposeAsync();
            await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(broker.SectionDisposals, Is.EqualTo(2));
        }
        await using (var broker = new BlockBrokerFixture { DisconnectOnScan = true })
        {
            await using var session = await MftIndexSession.OpenAsync([broker.Drive('X')],
                broker.ConnectAsync, broker.CacheDirectory, CancellationToken.None);
            Assert.That(session.IsFaulted, Is.True, "Broker death during opening must remain latched.");
            Assert.That(session.ScanErrors, Has.Count.EqualTo(1));
        }
        await using (var broker = new BlockBrokerFixture())
        {
            await using var session = await MftIndexSession.OpenSelectedVolumesAsync(["X", "Y"],
                _ => throw new IOException("identity denied"), broker.ConnectAsync,
                broker.CacheDirectory, CancellationToken.None);
            Assert.That(session.ScanErrors, Has.Count.EqualTo(2));
            Assert.That(broker.ConnectionCount, Is.Zero);
        }
    }

    [Test]
    public async Task UsesDirectoryProfileAndMarkerRetention()
    {
        await using var broker = new BlockBrokerFixture();
        await using var session = await MftIndexSession.OpenAsync([broker.Drive('X')],
            broker.ConnectAsync, broker.CacheDirectory, CancellationToken.None);
        Assert.That(broker.ScanFrame.DrivesSpec!.Split(':')[^1],
            Is.EqualTo(((int)BrokerScanProfile.DirectoryIndex).ToString(CultureInfo.InvariantCulture)));
        Assert.That(broker.ScanFrame.KeepFileNames, Is.EqualTo(new[] { ".git" }));
        Assert.That(session.Index.Drives.Single().ProducerKind, Is.EqualTo(ProducerKind.Mft));
    }

    [Test]
    public async Task NoCacheRequestsDeleteOnClose()
    {
        await using var broker = new BlockBrokerFixture();
        var session = await MftIndexSession.OpenAsync([broker.Drive('X')],
            broker.ConnectAsync, broker.CacheDirectory, CancellationToken.None);
        var options = broker.SectionOptions.Single();
        Assert.That(options.DeleteOnClose, Is.True);
        Assert.That(session.Index.CacheDirectoryPath, Is.EqualTo(broker.CacheDirectory));
        Assert.That(Directory.GetFiles(broker.CacheDirectory), Is.Empty);
        await session.DisposeAsync();
        Assert.That(File.Exists(options.Path), Is.False);
    }

    sealed class CountingLifetime(Action onDispose) : IAsyncDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count { get; private set; }

        public async ValueTask DisposeAsync()
        {
            Count++;
            onDispose();
            Started.SetResult();
            await Release.Task;
        }
    }
}
