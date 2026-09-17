using GitWizardTests.TestSupport;

namespace GitWizardTests;

[TestFixture]
public class BlockBrokerFixtureTests
{
    [Test]
    public async Task PeerDisposalStopsScanWithoutCancellation()
    {
        await using var broker = new BlockBrokerFixture();
        var client = await broker.ConnectAsync(CancellationToken.None);
        var midScanReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.MidScanAsync = () =>
        {
            midScanReached.SetResult();
            return resumeScan.Task;
        };
        var scan = client.ArmScanAndCatchUpAsync(["X"], broker.CreateOptions(["X"]));
        try
        {
            await Task.WhenAny(midScanReached.Task, scan).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(midScanReached.Task.IsCompletedSuccessfully, Is.True);
        }
        finally
        {
            try
            {
                await client.DisposeAsync();
            }
            finally
            {
                resumeScan.TrySetResult();
            }
        }
        Assert.That(async () => await scan.WaitAsync(TimeSpan.FromSeconds(10)), Throws.Exception);
        await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(broker.ClientCancellation.IsCancellationRequested, Is.False);
        Assert.That(broker.RowsWritten, Is.EqualTo(1));
        Assert.That(broker.ServedToCompletion, Is.False);
    }

    [TestCase(typeof(InvalidOperationException), false)]
    [TestCase(typeof(IOException), false)]
    [TestCase(typeof(ObjectDisposedException), false)]
    [TestCase(typeof(OperationCanceledException), false)]
    [TestCase(typeof(InvalidOperationException), true)]
    [TestCase(typeof(IOException), true)]
    [TestCase(typeof(ObjectDisposedException), true)]
    [TestCase(typeof(OperationCanceledException), true)]
    public async Task CallbackFailurePropagatesThroughTeardown(Type exceptionType, bool midScan)
    {
        var failure = (Exception)Activator.CreateInstance(exceptionType, "Injected fixture failure")!;
        var broker = new BlockBrokerFixture();
        using var cancellation = new CancellationTokenSource();
        await using var client = await broker.ConnectAsync(cancellation.Token);
        if (midScan)
            broker.MidScanAsync = () => Task.FromException(failure);
        else
            broker.ScanReceived = () => throw failure;
        var scan = client.ArmScanAndCatchUpAsync(["X"], broker.CreateOptions(["X"]), cancellation.Token);
        try
        {
            await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(broker.RowsWritten, Is.EqualTo(midScan ? 1 : 0));
            Assert.That(broker.ServedToCompletion, Is.False);
            Assert.That(Assert.CatchAsync(async () => await broker.DisconnectAsync()), Is.SameAs(failure));
        }
        finally
        {
            await cancellation.CancelAsync();
            Assert.That(async () => await scan.WaitAsync(TimeSpan.FromSeconds(10)), Throws.Exception);
            Assert.That(Assert.CatchAsync(async () => await broker.DisposeAsync()), Is.SameAs(failure));
        }
    }
}
