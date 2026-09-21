using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests.Watch;

[TestFixture]
public class JournalHintBuilderTests
{
    [Test]
    public void CheckpointTrimmed_ShowsSuggestedSizeAndCurrentMaximum()
    {
        var loss = FakeJournalSettingsSource.TrimmedLoss('D', 32L * 1024 * 1024, 256L * 1024 * 1024);

        var hint = JournalHintBuilder.BuildHint(loss);

        Assert.That(hint, Does.StartWith("Hint on D:"));
        Assert.That(hint, Does.Contain("full rescan was needed"));
        Assert.That(hint, Does.Contain("a journal of at least 256 MB would have avoided this rescan"));
        Assert.That(hint, Does.Contain("it is 32 MB now"));
        Assert.That(hint, Does.Contain("increase the journal size"));
    }

    [Test]
    public void JournalRecreated_ShowsNoSizeAndNoGrowSuggestion()
    {
        var loss = FakeJournalSettingsSource.RecreatedLoss('C', 64L * 1024 * 1024);

        var hint = JournalHintBuilder.BuildHint(loss);

        Assert.That(hint, Does.StartWith("Hint on C:"));
        Assert.That(hint, Does.Contain("The change journal was recreated"));
        Assert.That(hint, Does.Contain("Nothing needs changing."));
        Assert.That(hint, Does.Not.Contain("increase the journal size"));
    }

    [Test]
    public void CheckpointTrimmed_WithoutSize_ReportsLossWithoutSizeOrGrowOffer()
    {
        var loss = new JournalCheckpointLoss
        {
            DriveLetter = 'E',
            DetectedDuring = JournalCheckpointLossDetection.DriveOpening,
            Cause = JournalCheckpointLossCause.CheckpointTrimmed,
            CheckpointUsn = 1_000_000,
            FirstUsn = 5_000_000,
            NextUsn = 10_000_000,
            AllocationDelta = 64,
            MaximumSize = 8L * 1024 * 1024,
            BytesBehind = 4_000_000,
            SizeThatWouldHaveRetained = null
        };

        var hint = JournalHintBuilder.BuildHint(loss);

        Assert.That(hint, Does.StartWith("Hint on E:"));
        Assert.That(hint, Does.Contain("3.81 MB"));
        Assert.That(hint, Does.Contain("full rescan was needed"));
        Assert.That(hint, Does.Contain("too large to represent"));
        Assert.That(hint, Does.Not.Contain("increase the journal size"));
    }

    [Test]
    public void CheckpointTrimmed_IncludesBytesBehind()
    {
        var loss = new JournalCheckpointLoss
        {
            DriveLetter = 'E',
            DetectedDuring = JournalCheckpointLossDetection.DriveOpening,
            Cause = JournalCheckpointLossCause.CheckpointTrimmed,
            CheckpointUsn = 1_000_000,
            FirstUsn = 5_000_000,
            NextUsn = 10_000_000,
            AllocationDelta = 64,
            MaximumSize = 8L * 1024 * 1024,
            BytesBehind = 4_000_000,
            SizeThatWouldHaveRetained = 9_000_000
        };

        var hint = JournalHintBuilder.BuildHint(loss);

        Assert.That(hint, Does.Contain("3.81 MB"));
    }

    [Test]
    public void LiveWatchCheckpointTrimmed_ExplainsStoppedUpdatesAndRescanToCatchUp()
    {
        var loss = FakeJournalSettingsSource.TrimmedLoss('D', 32L * 1024 * 1024, 256L * 1024 * 1024,
            detectedDuring: JournalCheckpointLossDetection.LiveWatch);

        var hint = JournalHintBuilder.BuildHint(loss);

        Assert.That(hint, Does.StartWith("Hint on D:"));
        Assert.That(hint, Does.Contain("Live updates for this drive stopped"));
        Assert.That(hint, Does.Contain("moved 32 MB past the position the watch had reached"));
        Assert.That(hint, Does.Contain("a rescan of the drive is needed to catch up"));
        Assert.That(hint, Does.Contain("a journal of at least 256 MB would have avoided this rescan"));
        Assert.That(hint, Does.Contain("it is 32 MB now"));
        Assert.That(hint, Does.Not.Contain("full rescan was needed"));
    }

    [Test]
    public void LiveWatchCheckpointTrimmed_WithoutSize_ReportsLossWithoutSizeOrGrowOffer()
    {
        var loss = new JournalCheckpointLoss
        {
            DriveLetter = 'E',
            DetectedDuring = JournalCheckpointLossDetection.LiveWatch,
            Cause = JournalCheckpointLossCause.CheckpointTrimmed,
            CheckpointUsn = 1_000_000,
            FirstUsn = 5_000_000,
            NextUsn = 10_000_000,
            AllocationDelta = 64,
            MaximumSize = 8L * 1024 * 1024,
            BytesBehind = 4_000_000,
            SizeThatWouldHaveRetained = null
        };

        var hint = JournalHintBuilder.BuildHint(loss);

        Assert.That(hint, Does.Contain("a rescan of the drive is needed to catch up"));
        Assert.That(hint, Does.Contain("too large to represent"));
        Assert.That(hint, Does.Not.Contain("increase the journal size"));
    }

    [Test]
    public void LiveWatchJournalRecreated_ExplainsStoppedUpdatesWithNoSizeOffered()
    {
        var loss = FakeJournalSettingsSource.RecreatedLoss('C', 64L * 1024 * 1024,
            detectedDuring: JournalCheckpointLossDetection.LiveWatch);

        var hint = JournalHintBuilder.BuildHint(loss);

        Assert.That(hint, Does.StartWith("Hint on C:"));
        Assert.That(hint, Does.Contain("The change journal was recreated"));
        Assert.That(hint, Does.Contain("live updates for this drive stopped"));
        Assert.That(hint, Does.Contain("A rescan of the drive is needed to catch up"));
        Assert.That(hint, Does.Contain("Nothing needs changing."));
        Assert.That(hint, Does.Not.Contain("increase the journal size"));
    }
}
