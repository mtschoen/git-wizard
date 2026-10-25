using GitWizard.Watch;

namespace GitWizardTests.TestSupport;

[TestFixture]
public class ScriptedVolumeChangeSourceTests
{
    [Test]
    public async Task ArmGatePreservesErrorsAndWatchRemainsCancelable()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new ScriptedVolumeChangeSource(errors: new Dictionary<string, string> { ["X"] = "denied" })
        {
            ArmGate = release.Task,
            KeepWatching = true
        };
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        var arming = source.ArmAndCatchUpAsync(["X"], cancellation.Token);
        await source.ArmStarted.WaitAsync(TestTimeouts.HangGuard);
        Assert.That(arming.IsCompleted, Is.False);
        release.SetResult();
        var result = await arming.WaitAsync(TestTimeouts.HangGuard);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Errors["X"], Is.EqualTo("denied"));
            Assert.That(source.IsArmed, Is.True);
        }
        await using var reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await source.WatchStarted.WaitAsync(TestTimeouts.HangGuard);
        await cancellation.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await pending);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LateSubscriptionReplaysOnlyWhenRequested(bool replay)
    {
        var source = new ScriptedVolumeChangeSource { ReplayDeathToLateSubscribers = replay };
        source.KillSource("ended before subscription");
        string? observed = null;
        source.SourceDied += reason => observed = reason;
        Assert.That(observed, Is.EqualTo(replay ? "ended before subscription" : null));
        await source.DisposeAsync();
        Assert.That(source.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public async Task FiniteScriptPreservesBatchOrderAndCompletes()
    {
        var batches = new[]
        {
            new VolumeChangeBatch("X", [new VolumeChangeEntry(@"X:\repo\first", VolumeEntryKind.Created)]),
            new VolumeChangeBatch("X", [new VolumeChangeEntry(@"X:\repo\second", VolumeEntryKind.Modified)])
        };
        await using var source = new ScriptedVolumeChangeSource(batches);
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        await source.ArmAndCatchUpAsync(["X"], cancellation.Token);
        var observed = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cancellation.Token))
        {
            observed.Add(batch);
        }

        Assert.That(observed, Is.EqualTo(batches));
    }

    [Test]
    public async Task CancelingArmDoesNotPublishSuccessfulArming()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new ScriptedVolumeChangeSource { ArmGate = gate.Task };
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        var arming = source.ArmAndCatchUpAsync(["X"], cancellation.Token);
        await source.ArmStarted.WaitAsync(TestTimeouts.HangGuard);
        await cancellation.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await arming);
        Assert.That(source.IsArmed, Is.False);
    }
}
