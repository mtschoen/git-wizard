using System.Collections.Concurrent;
using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;
using MFTLibTestExtensions;
using static GitWizardTests.TestSupport.AsyncCondition;

namespace GitWizardTests.Watch;

// A loss reported while a drive recovers is one episode: the hint the source publishes for it
// follows the drive's watch state until the episode ends, without becoming a new episode.
[TestFixture]
public class JournalWarningRefreshTests
{
    const string RescanInProgress = "is being rescanned";
    const string RescanFinished = "was rescanned and the watch has caught up";
    const string RecoveryStopped = "Automatic recovery stopped";

    sealed class Harness : IAsyncDisposable
    {
        readonly PackedIndexFixture _files = new();
        readonly ScriptedWatchSource _scripted = new() { CatchUpOnStart = true };
        readonly ConcurrentQueue<JournalWarning> _warnings = new();
        MftIndexSession? _owner;

        public IndexVolumeChangeSource? Source { get; private set; }
        public MftIndexSession? Owner => _owner;
        public PackedIndexFixture Files => _files;
        public ScriptedWatchSource Scripted => _scripted;
        public JournalWarning[] Warnings => _warnings.ToArray();

        public async Task<VolumeArmResult> ArmAsync()
        {
            _files.AddMarker("repo");
            var index = await _files.OpenMftIndexAsync(_scripted, 'X');
            _owner = new MftIndexSession(index, null, ["X"], [new IndexedDrive('X', _files.TreePath, 12345u)],
                new Dictionary<string, string>());
            Source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(_owner));
            Source.JournalWarningDetected += _warnings.Enqueue;
            return await Source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        }

        public static JournalCheckpointLoss ScanCatchUpLoss() =>
            SyntheticCheckpointLoss.WithJournalPositions(
                SyntheticNotifications.CreateJournalCheckpointLoss('X', JournalCheckpointLossCause.CheckpointTrimmed,
                    JournalCheckpointLossDetection.ScanCatchUp,
                    new UsnJournalSettings { MaximumSize = 1024, AllocationDelta = 64 }), 1000, 2000, 3000);

        public async ValueTask DisposeAsync()
        {
            if (Source is not null)
            {
                await Source.DisposeAsync();
            }

            if (_owner is not null)
            {
                await _owner.DisposeAsync();
            }

            _files.Dispose();
        }
    }

    [Test]
    public async Task LossReportedWhileRecovering_IsRefreshedWhenTheWatchCatchesUp()
    {
        await using var harness = await StartedHarness(recovers: true);

        harness.Scripted.WatchFor('X').FailDrive(new IOException("X failed"));

        await WaitUntilAsync(() => harness.Warnings.Any(warning => warning.WatchState == WatchState.CaughtUp));
        var warnings = harness.Warnings;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warnings[0].Message, Does.Contain(RescanInProgress));
            Assert.That(warnings[^1].Message, Does.Contain(RescanFinished));
            Assert.That(warnings[^1].Message, Does.Not.Contain(RescanInProgress));
            Assert.That(warnings.Select(warning => warning.Loss).Distinct().Count(), Is.EqualTo(1),
                "a refreshed warning stays the same episode");
        }
    }

    [Test]
    public async Task CurrentJournalWarnings_HoldTheLatestStateForALateSubscriber()
    {
        await using var harness = await StartedHarness(recovers: true);

        harness.Scripted.WatchFor('X').FailDrive(new IOException("X failed"));

        // Read the snapshot, never the event stream: a subscriber that arrives now sees the end state.
        await WaitUntilAsync(() => harness.Source!.CurrentJournalWarnings.Any(warning => warning.WatchState == WatchState.CaughtUp));
        var current = harness.Source!.CurrentJournalWarnings.Single();
        Assert.That(current.Message, Does.Contain(RescanFinished));
    }

    [Test]
    public async Task RecoveryFaultAfterAPublishedLoss_UpdatesTheHintToTheStoppedWording()
    {
        await using var harness = await StartedHarness(recovers: false);

        harness.Scripted.WatchFor('X').FailDrive(new IOException("X failed"));

        await WaitUntilAsync(() => harness.Warnings.Any(warning => warning.IsRecoveryStopped));
        var warnings = harness.Warnings;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warnings[^1].Message, Does.Contain(RecoveryStopped));
            Assert.That(warnings[^1].Message, Does.Not.Contain(RescanInProgress));
            Assert.That(warnings.Select(warning => warning.Loss).Distinct().Count(), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ArmTimeWarnings_DescribeTheDrivesAfterCatchUp()
    {
        await using var harness = new Harness();
        var lossPending = 1;
        harness.Files.CatchUpLoss = _ => Interlocked.Exchange(ref lossPending, 0) == 1 ? Harness.ScanCatchUpLoss() : null;

        var armResult = await harness.ArmAsync();

        var warning = armResult.JournalWarnings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.WatchState, Is.EqualTo(WatchState.CaughtUp));
            Assert.That(warning.Message, Does.Contain(RescanFinished));
        }
    }

    [Test]
    public async Task RefreshOfADismissedEpisode_PublishesNothing()
    {
        await using var harness = new Harness();
        await harness.ArmAsync();
        var source = harness.Source!;
        var loss = Harness.ScanCatchUpLoss();
        var drive = harness.Owner!.Index.Drives.Single();
        var recovering = drive.Watch with { CheckpointLoss = loss, State = WatchState.Recovering, StateVersion = drive.Watch.StateVersion + 1 };
        source.ProcessFaultedDrive(drive with { Watch = recovering }, harness.Owner, new GitWizardConfiguration());
        var dismissed = new GitWizardConfiguration { IgnoredJournalWarnings = [12345u] };

        source.RefreshJournalWarning('X', recovering with { State = WatchState.CaughtUp, StateVersion = recovering.StateVersion + 1 }, dismissed);
        source.RefreshJournalWarning('X', recovering with { State = WatchState.CaughtUp, StateVersion = recovering.StateVersion + 1, CheckpointLoss = Harness.ScanCatchUpLoss() }, new GitWizardConfiguration());

        Assert.That(harness.Warnings, Has.Length.EqualTo(1), "a dismissed episode is not resurrected and another loss is not this episode");
    }

    static async Task<Harness> StartedHarness(bool recovers)
    {
        var harness = new Harness();
        var lossPending = 1;
        // The library retains one loss instance across its recovery attempts.
        var retained = Harness.ScanCatchUpLoss();
        harness.Files.CatchUpLoss = _ => !recovers || Interlocked.Exchange(ref lossPending, 0) == 1 ? retained : null;
        // The catch-up at start must succeed so the loss belongs to the injected fault, not the arm.
        var armLoss = harness.Files.CatchUpLoss;
        harness.Files.CatchUpLoss = null;
        await harness.ArmAsync();
        harness.Files.CatchUpLoss = armLoss;
        return harness;
    }
}
