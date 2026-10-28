using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Watch;

[TestFixture]
public class DirectoryRootWatchTests
{
    [Test]
    public async Task SiblingViewsOnOneVolumeMustBothCatchUpBeforeReady()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("first/repo");
        files.AddMarker("second/repo");
        var firstRoot = RootKey.FromDirectory(Path.Combine(files.TreePath, "first"));
        var secondRoot = RootKey.FromDirectory(Path.Combine(files.TreePath, "second"));
        var firstSource = new ScriptedWatchSource();
        var secondSource = new ScriptedWatchSource();
        var first = await files.OpenMftIndexAsync(firstSource, 'X', firstRoot);
        var second = await files.OpenMftIndexAsync(secondSource, 'X', secondRoot);
        var roots = new[] { firstRoot.ToString(), secondRoot.ToString() };
        var owner = new MftIndexSession([first, second], null, roots, new Dictionary<string, string>());
        await using var source = IndexVolumeChangeSource.CreatePrepared(owner);
        source.StartStartupBuffering();
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        var arming = source.ArmAndCatchUpAsync(roots, cancellation.Token);
        await Task.WhenAll(firstSource.WaitForStartAsync('X', 1, cancellation.Token),
            secondSource.WaitForStartAsync('X', 1, cancellation.Token));
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.WatchStateChanged += change =>
        {
            if (change.Watch.State == WatchState.CaughtUp)
            {
                applied.TrySetResult();
            }
        };
        await firstSource.WatchFor('X').PublishCaughtUpAsync();
        await applied.Task.WaitAsync(cancellation.Token);
        await source.WaitForCatchUpWorkForTestAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(arming.IsCompleted, Is.False);
            Assert.That(source.Readiness.CaughtUpVolumes, Is.Empty);
        }

        await secondSource.WatchFor('X').PublishCaughtUpAsync();
        await arming;
        Assert.That(source.Readiness.CaughtUpVolumes, Is.EqualTo(["X"]));

        var firstApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Changed += _ => firstApplied.TrySetResult();
        second.Changed += _ => secondApplied.TrySetResult();
        var change = PackedIndexFixture.Entry(100, 6, "file.txt", SyntheticJournalReason.FileCreate);
        var cursor = new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1002);
        await firstSource.WatchFor('X').PublishBatchAsync([change], cursor);
        await secondSource.WatchFor('X').PublishBatchAsync([change], cursor);
        await Task.WhenAll(firstApplied.Task, secondApplied.Task).WaitAsync(cancellation.Token);
        Assert.That(source.CompleteStartupBuffer().SelectMany(batch => batch.Entries)
            .Count(entry => entry.FullPath.EndsWith("file.txt", StringComparison.Ordinal)), Is.EqualTo(1));
    }
}
