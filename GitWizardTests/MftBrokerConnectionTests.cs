using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib;

namespace GitWizardTests;

[TestFixture]
public class MftBrokerConnectionTests
{
    [Test]
    public async Task DisposeBeforeFirstUseNeverConnects()
    {
        await using var broker = new BlockBrokerFixture();
        var holder = new MftBrokerConnection(broker.ConnectAsync);
        await holder.DisposeAsync();
        await holder.DisposeAsync();
        Assert.That(broker.ConnectionCount, Is.Zero);
        Assert.Throws<ObjectDisposedException>(() => holder.ConnectAsync(CancellationToken.None));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DisposalOwnsClientReturnedAfterCancellation(bool cancelCaller)
    {
        await using var broker = new BlockBrokerFixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<BrokerProcess>> connectBroker = broker.ConnectAsync;
        var holder = new MftBrokerConnection(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            // A launcher can complete despite cancellation; ownership must still be reclaimed.
            return await connectBroker(CancellationToken.None);
        });
        var connecting = holder.ConnectAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (cancelCaller)
            await cancellation.CancelAsync();
        var first = holder.DisposeAsync().AsTask();
        var second = holder.DisposeAsync().AsTask();
        try
        {
            Assert.That(first.IsCompleted, Is.False);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.That(async () => await connecting, Throws.InstanceOf<OperationCanceledException>());
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(broker.ConnectionCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ProcessThatEndedBeforeSubscriptionLatchesTheFault()
    {
        await using var broker = new BlockBrokerFixture();
        Func<CancellationToken, Task<BrokerProcess>> connectBroker = broker.ConnectAsync;
        var holder = new MftBrokerConnection(async token =>
        {
            var process = await connectBroker(token);
            await process.DisposeAsync();
            return process;
        });
        var process = await holder.ConnectAsync(CancellationToken.None);
        Assert.That(process.HasEnded, Is.True);
        Assert.That(holder.IsFaulted, Is.True);
        await holder.DisposeAsync();
    }

    [Test]
    public async Task ConcurrentConnectSharesOneOwnedClient()
    {
        await using var broker = new BlockBrokerFixture();
        var holder = new MftBrokerConnection(broker.ConnectAsync);
        var clients = await Task.WhenAll(holder.ConnectAsync(CancellationToken.None),
            holder.ConnectAsync(CancellationToken.None));
        Assert.That(clients[0], Is.SameAs(clients[1]));
        await holder.DisposeAsync();
        await holder.DisposeAsync();
        await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(broker.ConnectionCount, Is.EqualTo(1));
    }

    [Test]
    public async Task FailedFactoryDoesNotPreventCleanup()
    {
        var holder = new MftBrokerConnection(_ =>
            Task.FromException<BrokerProcess>(new IOException("launch failed")));
        Assert.ThrowsAsync<IOException>(() => holder.ConnectAsync(CancellationToken.None));
        await holder.DisposeAsync();
        Assert.That(holder.Process, Is.Null);
    }

    [Test]
    public async Task CancelledConnectAttemptDoesNotPoisonLaterConnect()
    {
        await using var broker = new BlockBrokerFixture();
        using var cancellation = new CancellationTokenSource();
        var (holder, entered, release) = CreateGatedHolder(broker);
        var cancelled = holder.ConnectAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        Assert.That(async () => await cancelled, Throws.InstanceOf<OperationCanceledException>());
        release.TrySetResult();
        var client = await holder.ConnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(client, Is.SameAs(holder.Process));
        await holder.DisposeAsync();
        await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(broker.ConnectionCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CancelledCallerDoesNotCancelConcurrentCaller()
    {
        await using var broker = new BlockBrokerFixture();
        using var cancellation = new CancellationTokenSource();
        var (holder, entered, release) = CreateGatedHolder(broker);
        var cancelled = holder.ConnectAsync(cancellation.Token);
        var patient = holder.ConnectAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        Assert.That(async () => await cancelled, Throws.InstanceOf<OperationCanceledException>());
        release.TrySetResult();
        var client = await patient.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(client, Is.SameAs(holder.Process));
        await holder.DisposeAsync();
        await broker.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(broker.ConnectionCount, Is.EqualTo(1));
    }

    static (MftBrokerConnection Holder, TaskCompletionSource Entered, TaskCompletionSource Release)
        CreateGatedHolder(BlockBrokerFixture broker)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<BrokerProcess>> connectBroker = broker.ConnectAsync;
        var holder = new MftBrokerConnection(async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return await connectBroker(token);
        });
        return (holder, entered, release);
    }

    [Test]
    public async Task PreCancelledConnectionDoesNotLaunch()
    {
        await using var broker = new BlockBrokerFixture();
        var holder = new MftBrokerConnection(broker.ConnectAsync);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => holder.ConnectAsync(cancelled.Token));
        await holder.DisposeAsync();
        Assert.That(broker.ConnectionCount, Is.Zero);
    }
}
