using System.Collections.Concurrent;
using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;
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
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);
    static readonly SyntheticJournalWindow Healthy = new(PackedIndexFixture.JournalId, 0, 5_000, 64, 8_192);
    static readonly SyntheticJournalWindow Lost = new(PackedIndexFixture.JournalId, 1_500, 5_000, 64, 8_192);

    [Test]
    public async Task MidWatchCheckpointLoss_RecoversTheDriveWhileTheHealthyDriveKeepsDelivering()
    {
        var windows = new ConcurrentDictionary<char, SyntheticJournalWindow>();
        windows['X'] = Healthy;
        windows['Y'] = Healthy;
        using var scope = JournalIsolation.OverrideJournalWindow(
            drive => windows.TryGetValue(drive, out var window) ? window : null);
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var owner = new MftIndexSession(await files.OpenMftIndexAsync(scripted, 'X', 'Y'), null, ["X", "Y"],
            [new IndexedDrive('X', files.TreePath, 1), new IndexedDrive('Y', files.TreePath, 1)],
            new Dictionary<string, string>());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.Producer.BeforeProduceAsync = (_, token) => release.Task.WaitAsync(token);
        var warnings = new ConcurrentQueue<JournalWarning>();
        using var cancellation = new CancellationTokenSource(HangGuard);
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
                owner.Index.Drives.Single(drive => drive.DriveLetter == 'X').WatchCatchUp ==
                WatchCatchUpState.Recovering &&
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
                    Is.EqualTo(new[] { JournalCheckpointLossDetection.LiveWatch }));
            });

            await scripted.WatchFor('Y').PublishBatchAsync([IndexVolumeChangeSourceTests.Entry(50, 6, "while-x-recovers", UsnReason.FileCreate)], new UsnJournalCursor(PackedIndexFixture.JournalId, 1002));
            Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(HangGuard), Is.True);
            Assert.That(reader.Current.Volume, Is.EqualTo("Y"));

            // The recovery rescan reads a journal that holds its new cursor.
            windows['X'] = Healthy;
            release.SetResult();
            await WaitUntilAsync(() => source.Readiness.TryGet("X", out var caughtUp) &&
                                       caughtUp.State == VolumeWatchState.CaughtUp);
            await scripted.WatchFor('X').PublishBatchAsync([IndexVolumeChangeSourceTests.Entry(51, 6, "after-recovery", UsnReason.FileCreate)], new UsnJournalCursor(PackedIndexFixture.JournalId, 1003));

            Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(HangGuard), Is.True);
            Assert.That(reader.Current.Volume, Is.EqualTo("X"));
            Assert.That(scripted.Starts.Count(start => start.DriveLetter == 'X'), Is.EqualTo(2));
        }
        finally
        {
            if (reader is not null)
                await reader.DisposeAsync();
            await source.DisposeAsync();
            await owner.DisposeAsync();
        }
    }
}
