using System.Collections.Concurrent;
using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;
using static GitWizardTests.TestSupport.AsyncCondition;

namespace GitWizardTests.Watch;

/// <summary>
/// A <see cref="WatchFaultKind.CapacityExhausted" /> notification is not a watch failure: the drive
/// stays usable and keeps delivering changes while the user is told a rescan is the remedy. The log
/// sink is process-global, so the fixture is non-parallel.
/// </summary>
[TestFixture]
[NonParallelizable]
public class IndexVolumeChangeSourceCapacityTests
{
    /// <summary>A record number above 32 bits, which no block can store.</summary>
    const ulong OutOfRangeRecord = (ulong)uint.MaxValue + 1;

    [Test]
    public async Task CapacityExhaustedFault_WarnsAndKeepsTheDriveDeliveringChanges()
    {
        var messages = new ConcurrentQueue<(string? Message, GitWizardLog.LogType Type)>();
        var originalSink = GitWizardLog.TypedLogMethod;
        GitWizardLog.TypedLogMethod = (message, type) => messages.Enqueue((message, type));
        try
        {
            await using var harness = await IndexVolumeChangeSourceFaultTests.FaultHarness.StartAsync('X', 'Y');
            int[] notifications = [0];
            harness.Source.CapacityWarningsChanged += () => Interlocked.Increment(ref notifications[0]);
            await PublishUnstorableRecordAsync(harness);

            await WaitUntilAsync(() => messages.Any(entry => entry.Type == GitWizardLog.LogType.Warning &&
                                                             entry.Message!.Contains("rescan")));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(harness.Failures, Is.Empty);
                Assert.That(harness.Deaths, Is.Empty);
                Assert.That(harness.ReadinessOf('X'), Is.EqualTo(VolumeWatchState.CaughtUp));
                Assert.That(harness.StatusOf('X').Block!.IsCapacityExhausted, Is.True);
                Assert.That(messages.Single(entry => entry.Type == GitWizardLog.LogType.Warning).Message,
                    Does.Contain("X"));
                Assert.That(Volatile.Read(ref notifications[0]), Is.EqualTo(1));
                Assert.That(harness.Source.CurrentCapacityWarnings.Select(warning => warning.DriveLetter),
                    Is.EqualTo(["X"]), "Only the faulted drive carries the warning.");
                Assert.That(harness.Source.CurrentCapacityWarnings.Single().Detail,
                    Is.Not.Empty.And.Not.EqualTo(CapacityWarning.FlaggedBeforeWatchDetail), "The fault's own message.");
            }

            await harness.Scripted.WatchFor('X').PublishBatchAsync([
                PackedIndexFixture.Entry(61, 6, "later", SyntheticJournalReason.FileCreate)
            ], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1004));
            Assert.That(await harness.ReadAsync(), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(harness.Current.Volume, Is.EqualTo("X"));
                Assert.That(harness.Current.Entries.Single().FullPath, Is.EqualTo(@"X:\repo\later"));
                Assert.That(harness.Failures, Is.Empty);
                Assert.That(Volatile.Read(ref notifications[0]), Is.EqualTo(1), "A later change raises no second warning.");
            }
        }
        finally
        {
            GitWizardLog.TypedLogMethod = originalSink;
        }
    }

    [Test]
    public async Task WatchStartedOnAFlaggedBlock_WarnsAboutThatDriveOnly()
    {
        await using var harness = await IndexVolumeChangeSourceFaultTests.FaultHarness.StartAsync(['X', 'Y'], null,
            async indices =>
            {
                // A direct apply flags and skips without notifying: the block is flagged before any watch exists.
                var x = indices.Single(index => index.Status.Root == new VolumeKey('X'));
                await FileIndexTestAccess.ApplyJournalEntriesAsync(x, new VolumeKey('X'),
                    [PackedIndexFixture.Entry(OutOfRangeRecord, 6, "huge", SyntheticJournalReason.FileCreate)],
                    new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1001));
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.StatusOf('X').Block!.IsCapacityExhausted, Is.True);
            Assert.That(harness.Source.CurrentCapacityWarnings.Select(warning => warning.DriveLetter), Is.EqualTo(["X"]));
            Assert.That(harness.Source.CurrentCapacityWarnings.Single().Detail,
                Is.EqualTo(CapacityWarning.FlaggedBeforeWatchDetail));
            Assert.That(harness.Failures, Is.Empty);
            Assert.That(harness.ReadinessOf('X'), Is.EqualTo(VolumeWatchState.CaughtUp));
        }
    }

    [Test]
    public async Task RescanOfTheDrive_ClearsItsWarningAndLeavesTheOtherDrive()
    {
        await using var harness = await IndexVolumeChangeSourceFaultTests.FaultHarness.StartAsync('X', 'Y');
        await PublishUnstorableRecordAsync(harness);
        await WaitUntilAsync(() => harness.Source.CurrentCapacityWarnings.Count == 1);

        // MFTLib's recovery of a drive fault rescans the drive, publishing a block that is not flagged.
        harness.Inject("drive", 'X');
        await WaitUntilAsync(() => harness.Scripted.Watches.Count(watch => watch.Volume == 'X') == 2);
        await WaitUntilAsync(() => harness.ReadinessOf('X') == VolumeWatchState.CaughtUp &&
                                   harness.StatusOf('X').Watch.State == WatchState.CaughtUp);
        await WaitUntilAsync(() => harness.Source.CurrentCapacityWarnings.Count == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.StatusOf('X').Block!.IsCapacityExhausted, Is.False);
            Assert.That(harness.StatusOf('Y').Block!.IsCapacityExhausted, Is.False);
            Assert.That(harness.ReadinessOf('Y'), Is.EqualTo(VolumeWatchState.CaughtUp));
            Assert.That(harness.Failures, Is.Empty);
        }
    }

    static async Task PublishUnstorableRecordAsync(IndexVolumeChangeSourceFaultTests.FaultHarness harness)
    {
        await harness.Scripted.WatchFor('X').PublishBatchAsync([
            PackedIndexFixture.Entry(60, 6, "before", SyntheticJournalReason.FileCreate),
            PackedIndexFixture.Entry(OutOfRangeRecord, 6, "huge", SyntheticJournalReason.FileCreate)
        ], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1003));
        Assert.That(await harness.ReadAsync(), Is.True);
    }
}
