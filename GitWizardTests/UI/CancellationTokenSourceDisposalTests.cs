using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Cancellation tests exercise cooperative cancellation callback completion and disposal order.
// Covers CancellationTokenSourceDisposal.cs.
public class CancellationTokenSourceDisposalTests
{
    [Test]
    public async Task CancelAndDisposeAsync_DisposesOnlyAfterCancellationCallbacksComplete()
    {
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new CancellationTokenSource();
#pragma warning disable MA0045 // Synchronous block required inside cancellation callback to simulate in-flight callback
        await using var registration = source.Token.Register(() =>
        {
            callbackStarted.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        });
#pragma warning restore MA0045

        var cleanupTask = CancellationTokenSourceDisposal.CancelAndDisposeAsync(source);
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleanupTask.IsCompleted, Is.False,
                "Cleanup must wait for an in-flight cancellation callback.");
            Assert.DoesNotThrow(() => _ = source.Token,
                "The source must remain usable until cancellation callbacks finish.");
        }

        releaseCallback.TrySetResult();
        await cleanupTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Throws<ObjectDisposedException>(() => _ = source.Token);
    }
}
