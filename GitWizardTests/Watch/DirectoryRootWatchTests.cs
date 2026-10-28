using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Watch;

[TestFixture]
public class DirectoryRootWatchTests
{
    // The fixture tree numbers its directories in walk order after the root row (5): first, first/repo, first/repo/.git, second, second/repo, second/repo/.git.
    const ulong FirstDirectoryRow = 6;
    const ulong SecondDirectoryRow = 9;

    [Test]
    public async Task SiblingViewsOnOneVolumeShareOneWatchAndEachReceivesOnlyItsOwnChanges()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("first/repo");
        files.AddMarker("second/repo");
        var firstRoot = RootKey.FromDirectory(Path.Combine(files.TreePath, "first"));
        var secondRoot = RootKey.FromDirectory(Path.Combine(files.TreePath, "second"));
        var scripted = new ScriptedWatchSource();
        var indexSource = SyntheticIndexSource.Create(files.Producer, scripted);
        var first = await files.OpenMftIndexAsync(indexSource, 'X', firstRoot);
        var second = await files.OpenMftIndexAsync(indexSource, 'X', secondRoot);
        var roots = new[] { firstRoot.ToString(), secondRoot.ToString() };
        var owner = new MftIndexSession([first, second], null, roots, new Dictionary<string, string>());
        await using var source = IndexVolumeChangeSource.CreatePrepared(owner);
        source.StartStartupBuffering();
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        var arming = source.ArmAndCatchUpAsync(roots, cancellation.Token);
        await scripted.WaitForStartAsync('X', 1, cancellation.Token);
        await scripted.WatchFor('X').PublishCaughtUpAsync();
        await arming;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Readiness.CaughtUpVolumes, Is.EqualTo(["X"]));
            Assert.That(scripted.Starts, Has.Count.EqualTo(1), "both views share one watch");
        }

        var firstPaths = new List<string>();
        var secondPaths = new List<string>();
        var firstApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Changed += change =>
        {
            firstPaths.Add(change.Path);
            firstApplied.TrySetResult();
        };
        second.Changed += change =>
        {
            secondPaths.Add(change.Path);
            secondApplied.TrySetResult();
        };
        await scripted.WatchFor('X').PublishBatchAsync([
            PackedIndexFixture.Entry(100, FirstDirectoryRow, "inside-first.txt", SyntheticJournalReason.FileCreate),
            PackedIndexFixture.Entry(101, SecondDirectoryRow, "inside-second.txt", SyntheticJournalReason.FileCreate)
        ], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1002));
        await Task.WhenAll(firstApplied.Task, secondApplied.Task).WaitAsync(cancellation.Token);

        var delivered = source.CompleteStartupBuffer().SelectMany(batch => batch.Entries)
            .Select(entry => entry.FullPath).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstPaths, Has.Count.EqualTo(1));
            Assert.That(firstPaths[0], Does.EndWith("inside-first.txt"));
            Assert.That(secondPaths, Has.Count.EqualTo(1));
            Assert.That(secondPaths[0], Does.EndWith("inside-second.txt"));
            Assert.That(delivered.Count(path => path.EndsWith("inside-first.txt", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(delivered.Count(path => path.EndsWith("inside-second.txt", StringComparison.Ordinal)), Is.EqualTo(1));
        }
    }
}
