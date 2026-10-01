using GitWizardTests.TestSupport;

namespace GitWizardTests;

[TestFixture]
public class BlockBrokerFixtureTests
{
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ScanWritesTheFixtureRowsThroughTheRealHost()
    {
        await using var broker = new BlockBrokerFixture();
        var process = await broker.ConnectAsync(CancellationToken.None);
        var result = await process.ScanDriveAsync('X', broker.CreateTarget('X'), BlockBrokerFixture.CreateOptions(),
            CancellationToken.None).WaitAsync(HangGuard);
        using var block = result.Block.Block;
        Assert.That(result.DriveLetter, Is.EqualTo('X'));
        Assert.That(broker.ScannedDrives, Is.EqualTo(new[] { "X" }));
        Assert.That(broker.RowsWritten, Is.EqualTo(4));
        Assert.That(broker.ServedToCompletion, Is.True);
        Assert.That(broker.LastFilter?.KeepFileNames, Is.EqualTo(new[] { ".git" }));
    }

    [Test]
    public async Task ProcessDisposalDuringScanStopsTheScanAndEndsTheProcess()
    {
        await using var broker = new BlockBrokerFixture();
        var process = await broker.ConnectAsync(CancellationToken.None);
        var midScanReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.MidScanAsync = async hostToken =>
        {
            midScanReached.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, hostToken).ContinueWith(_ => { },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        };
        var scan = process.ScanDriveAsync('X', broker.CreateTarget('X'), BlockBrokerFixture.CreateOptions(),
            CancellationToken.None);
        await Task.WhenAny(midScanReached.Task, scan).WaitAsync(HangGuard);
        Assert.That(midScanReached.Task.IsCompletedSuccessfully, Is.True);

        await process.DisposeAsync();

        Assert.That(async () => await scan.WaitAsync(HangGuard), Throws.Exception);
        await broker.Shutdown.WaitAsync(HangGuard);
        Assert.That(process.Ended.IsCompletedSuccessfully, Is.True);
        Assert.That(broker.RowsWritten, Is.EqualTo(1));
        Assert.That(broker.ServedToCompletion, Is.False);
    }

    [Test]
    public async Task ScanErrorFailsOnlyTheScan()
    {
        await using var broker = new BlockBrokerFixture(scanError: "volume denied");
        var process = await broker.ConnectAsync(CancellationToken.None);
        var failure = Assert.CatchAsync(async () => await process.ScanDriveAsync('X', broker.CreateTarget('X'),
            BlockBrokerFixture.CreateOptions(), CancellationToken.None).WaitAsync(HangGuard));
        Assert.That(failure!.Message, Does.Contain("volume denied"));
        Assert.That(process.Ended.IsCompleted, Is.False);
        Assert.That(broker.SectionDisposals, Is.EqualTo(1));
    }
}
