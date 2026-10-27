using GitWizard.Watch;
using MFTLib;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Watch;

// The hint wording follows the drive's current recovery state: the retained loss records what
// happened, and only the state can support a claim about a rescan in progress or finished.
[TestFixture]
public class JournalHintStateTests
{
    const string RescanInProgress = "is being rescanned";
    const string RescanFinished = "was rescanned and the watch has caught up";
    const string WatchCatchingUp = "The watch is catching up with the change journal.";
    const string RecoveryStopped = "Automatic recovery stopped";
    const string StateUnknown = "The current watch state is unknown";

    static readonly JournalCheckpointLossDetection[] RecoverableDetections =
    [
        JournalCheckpointLossDetection.LiveWatch,
        JournalCheckpointLossDetection.ScanCatchUp
    ];

    static readonly JournalCheckpointLossCause[] Causes =
    [
        JournalCheckpointLossCause.CheckpointTrimmed,
        JournalCheckpointLossCause.JournalRecreated,
        JournalCheckpointLossCause.JournalAdvanced
    ];

    static IEnumerable<TestCaseData> RecoverableLosses() =>
        RecoverableDetections.SelectMany(_ => Causes, (detection, cause) =>
            new TestCaseData(cause, detection).SetName($"{cause}_{detection}"));

    static JournalCheckpointLoss CreateLoss(JournalCheckpointLossCause cause, JournalCheckpointLossDetection detection) =>
        SyntheticNotifications.CreateJournalCheckpointLoss('D', cause, detection,
            new UsnJournalSettings { MaximumSize = 8L * 1024 * 1024, AllocationDelta = 64 }) with
        {
            BytesBehind = cause == JournalCheckpointLossCause.CheckpointTrimmed ? 4_000_000 : null,
            SizeThatWouldHaveRetained = cause == JournalCheckpointLossCause.CheckpointTrimmed ? 9_000_000 : null
        };

    [TestCaseSource(nameof(RecoverableLosses))]
    public void Recovering_SaysTheRescanIsInProgress(JournalCheckpointLossCause cause, JournalCheckpointLossDetection detection)
    {
        var hint = JournalHintBuilder.BuildHint(CreateLoss(cause, detection), WatchState.Recovering, false);

        Assert.That(hint, Does.Contain(RescanInProgress));
        Assert.That(hint, Does.Not.Contain(RescanFinished));
        Assert.That(hint, Does.Not.Contain(RecoveryStopped));
    }

    [TestCaseSource(nameof(RecoverableLosses))]
    public void CaughtUp_SaysTheRescanCompleted(JournalCheckpointLossCause cause, JournalCheckpointLossDetection detection)
    {
        var hint = JournalHintBuilder.BuildHint(CreateLoss(cause, detection), WatchState.CaughtUp, false);

        Assert.That(hint, Does.Contain(RescanFinished));
        Assert.That(hint, Does.Not.Contain(RescanInProgress));
        Assert.That(hint, Does.Not.Contain(RecoveryStopped));
    }

    [TestCaseSource(nameof(RecoverableLosses))]
    public void CatchingUp_DescribesTheWatchWithoutClaimingARescan(JournalCheckpointLossCause cause, JournalCheckpointLossDetection detection)
    {
        var hint = JournalHintBuilder.BuildHint(CreateLoss(cause, detection), WatchState.CatchingUp, false);

        Assert.That(hint, Does.Contain(WatchCatchingUp));
        Assert.That(hint, Does.Not.Contain(RescanInProgress));
        Assert.That(hint, Does.Not.Contain(RescanFinished));
    }

    [TestCaseSource(nameof(RecoverableLosses))]
    public void RecoveryStopped_KeepsTheActionableAdviceWhateverTheState(JournalCheckpointLossCause cause, JournalCheckpointLossDetection detection)
    {
        foreach (var state in new WatchState?[] { null, WatchState.Faulted, WatchState.Recovering, WatchState.CaughtUp })
        {
            var hint = JournalHintBuilder.BuildHint(CreateLoss(cause, detection), state, true);

            Assert.That(hint, Does.Contain(RecoveryStopped));
            Assert.That(hint, Does.Contain("Rescan this drive."));
            Assert.That(hint, Does.Not.Contain(RescanInProgress));
            Assert.That(hint, Does.Not.Contain(RescanFinished));
        }
    }

    [TestCaseSource(nameof(RecoverableLosses))]
    public void UnknownState_ClaimsNeitherRecoveryNorThatNothingNeedsChanging(JournalCheckpointLossCause cause, JournalCheckpointLossDetection detection)
    {
        foreach (var state in new WatchState?[] { null, WatchState.NotStarted, WatchState.Faulted })
        {
            var hint = JournalHintBuilder.BuildHint(CreateLoss(cause, detection), state, false);

            Assert.That(hint, Does.Contain(StateUnknown));
            Assert.That(hint, Does.Not.Contain(RescanInProgress));
            Assert.That(hint, Does.Not.Contain(RescanFinished));
            Assert.That(hint, Does.Not.Contain("Nothing needs changing"));
            Assert.That(hint, Does.Not.Contain("scanned again"));
        }
    }

    [Test]
    public void DriveOpening_IgnoresTheWatchState()
    {
        foreach (var cause in Causes)
        {
            var loss = CreateLoss(cause, JournalCheckpointLossDetection.RootOpening);
            var recovering = JournalHintBuilder.BuildHint(loss, WatchState.Recovering, false);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(JournalHintBuilder.BuildHint(loss, null, false), Is.EqualTo(recovering));
                Assert.That(JournalHintBuilder.BuildHint(loss, WatchState.CaughtUp, true), Is.EqualTo(recovering));
                Assert.That(recovering, Does.Not.Contain(RescanInProgress));
            }
        }
    }

    [Test]
    public void JournalWarning_Message_FollowsTheWarningsRecoveryState()
    {
        var loss = CreateLoss(JournalCheckpointLossCause.JournalRecreated, JournalCheckpointLossDetection.LiveWatch);

        var recovering = new JournalWarning { DriveLetter = "D", VolumeSerial = 1, Loss = loss, WatchState = WatchState.Recovering };
        var stopped = recovering with { IsRecoveryStopped = true };
        var unknown = new JournalWarning { DriveLetter = "D", VolumeSerial = 1, Loss = loss };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovering.Message, Does.Contain(RescanInProgress));
            Assert.That(stopped.Message, Does.Contain(RecoveryStopped));
            Assert.That(unknown.Message, Does.Contain(StateUnknown));
        }
    }
}
