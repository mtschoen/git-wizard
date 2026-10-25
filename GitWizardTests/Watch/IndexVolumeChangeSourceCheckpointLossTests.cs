using System.Collections.Concurrent;
using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;
using static GitWizardTests.TestSupport.AsyncCondition;

namespace GitWizardTests.Watch;

/// <summary>
/// A checkpoint lost while a drive is watched, driven through the library's journal window
/// override rather than a hand-built status. The override is process-global, so the fixture is
/// non-parallel and the scope outlives every awaited step; the source and index are disposed
/// before it.
/// </summary>
[TestFixture]
[NonParallelizable]
public class IndexVolumeChangeSourceCheckpointLossTests
{
    static readonly SyntheticJournalWindow Healthy = new(PackedIndexFixture.JournalIdentifier, 0, 5_000, 64, 8_192);
    static readonly SyntheticJournalWindow Lost = new(PackedIndexFixture.JournalIdentifier, 1_500, 5_000, 64, 8_192);

    [Test]
    public async Task MidWatchCheckpointLoss_RecoversTheDriveWhileTheHealthyDriveKeepsDelivering()
    {
        var windows = new ConcurrentDictionary<char, SyntheticJournalWindow>();
        windows['X'] = Healthy;
        windows['Y'] = Healthy;
        using var scope = JournalIsolation.OverrideJournalWindow(
            drive => windows.TryGetValue(drive.Letter, out var window) ? window : null);
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = new MftIndexSession(await files.OpenMftIndexAsync(scripted, 'X', 'Y'), null, ["X", "Y"],
            [new IndexedDrive('X', files.TreePath, 1), new IndexedDrive('Y', files.TreePath, 1)],
            new Dictionary<string, string>());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeProduceAsync = (_, token) => release.Task.WaitAsync(token);
        var warnings = new ConcurrentQueue<JournalWarning>();
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        IAsyncEnumerator<VolumeChangeBatch>? reader = null;
        var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        try
        {
            source.JournalWarningDetected += warnings.Enqueue;
            await source.ArmAndCatchUpAsync(["X", "Y"], CancellationToken.None);
            reader = source.WatchAsync(cancellation.Token).GetAsyncEnumerator();

            windows['X'] = Lost;
            scripted.WatchFor('X').FailDrive(new IOException("X journal moved on"));
            await WaitUntilAsync(() =>
                owner.Index.Drives.Single(drive => drive.Drive == 'X').Watch.State ==
                WatchState.Recovering &&
                source.Readiness.TryGet("X", out var recovering) &&
                recovering.State == VolumeWatchState.CatchingUp &&
                warnings.Any(warning => warning.Loss.DetectedDuring == JournalCheckpointLossDetection.LiveWatch));

            Assert.Multiple(() =>
            {
                Assert.That(source.Readiness.TryGet("X", out var recovering), Is.True);
                Assert.That(recovering!.State, Is.EqualTo(VolumeWatchState.CatchingUp));
                Assert.That(source.Readiness.TryGet("Y", out var healthy), Is.True);
                Assert.That(healthy!.State, Is.EqualTo(VolumeWatchState.CaughtUp));
                Assert.That(warnings.Select(warning => warning.Loss.DetectedDuring),
                    Is.EqualTo([JournalCheckpointLossDetection.LiveWatch]));
            });

            await scripted.WatchFor('Y').PublishBatchAsync([IndexVolumeChangeSourceTests.Entry(50, 6, "while-x-recovers", SyntheticJournalReason.FileCreate)], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1002));
            Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(TestTimeouts.HangGuard), Is.True);
            Assert.That(reader.Current.Volume, Is.EqualTo("Y"));

            // The recovery rescan reads a journal that holds its new cursor.
            windows['X'] = Healthy;
            release.SetResult();
            await WaitUntilAsync(() => source.Readiness.TryGet("X", out var caughtUp) &&
                                       caughtUp.State == VolumeWatchState.CaughtUp);
            await scripted.WatchFor('X').PublishBatchAsync([IndexVolumeChangeSourceTests.Entry(51, 6, "after-recovery", SyntheticJournalReason.FileCreate)], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1003));

            Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(TestTimeouts.HangGuard), Is.True);
            Assert.That(reader.Current.Volume, Is.EqualTo("X"));
            Assert.That(scripted.Starts.Count(start => start.Drive == 'X'), Is.EqualTo(2));

            // The hint the consumers hold follows the drive to the end of the same episode.
            await WaitUntilAsync(() => warnings.Last().WatchState == WatchState.CaughtUp);
            var finalWarnings = warnings.ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(finalWarnings.Select(warning => warning.Loss).Distinct().Count(), Is.EqualTo(1));
                Assert.That(finalWarnings.Last().Message, Does.Contain("was rescanned and the watch has caught up"));
                Assert.That(finalWarnings.Last().Message, Does.Not.Contain("is being rescanned"));
            });
        }
        finally
        {
            if (reader is not null)
            {
                await reader.DisposeAsync();
            }

            await source.DisposeAsync();
            await owner.DisposeAsync();
        }
    }
}
