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
            await harness.Scripted.WatchFor('X').PublishBatchAsync([
                PackedIndexFixture.Entry(60, 6, "before", SyntheticJournalReason.FileCreate),
                PackedIndexFixture.Entry(OutOfRangeRecord, 6, "huge", SyntheticJournalReason.FileCreate)
            ], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1003));
            Assert.That(await harness.ReadAsync(), Is.True);

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
            }
        }
        finally
        {
            GitWizardLog.TypedLogMethod = originalSink;
        }
    }
}
