using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

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
        var loss = SyntheticNotifications.CreateJournalCheckpointLoss('E', JournalCheckpointLossCause.CheckpointTrimmed, JournalCheckpointLossDetection.DriveOpening, 64, 8L * 1024 * 1024) with { BytesBehind = 4_000_000, SizeThatWouldHaveRetained = null };

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
        var loss = SyntheticNotifications.CreateJournalCheckpointLoss('E', JournalCheckpointLossCause.CheckpointTrimmed, JournalCheckpointLossDetection.DriveOpening, 64, 8L * 1024 * 1024) with { BytesBehind = 4_000_000, SizeThatWouldHaveRetained = 9_000_000 };

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
        Assert.That(hint, Does.Contain("the drive is being rescanned to catch up"));
        Assert.That(hint, Does.Contain("a journal of at least 256 MB would have avoided this rescan"));
        Assert.That(hint, Does.Contain("it is 32 MB now"));
        Assert.That(hint, Does.Not.Contain("full rescan was needed"));
    }

    [Test]
    public void LiveWatchCheckpointTrimmed_WithoutSize_ReportsLossWithoutSizeOrGrowOffer()
    {
        var loss = SyntheticNotifications.CreateJournalCheckpointLoss('E', JournalCheckpointLossCause.CheckpointTrimmed, JournalCheckpointLossDetection.LiveWatch, 64, 8L * 1024 * 1024) with { BytesBehind = 4_000_000, SizeThatWouldHaveRetained = null };

        var hint = JournalHintBuilder.BuildHint(loss);

        Assert.That(hint, Does.Contain("the drive is being rescanned to catch up"));
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
        Assert.That(hint, Does.Contain("The drive is being rescanned to catch up"));
        Assert.That(hint, Does.Contain("Nothing needs changing."));
        Assert.That(hint, Does.Not.Contain("increase the journal size"));
    }

    [Test]
    public void ScanCatchUpCheckpointTrimmed_ExplainsTheRepeatedScanAndSuggestsSize()
    {
        var loss = FakeJournalSettingsSource.TrimmedLoss('D', 32L * 1024 * 1024, 256L * 1024 * 1024,
            detectedDuring: JournalCheckpointLossDetection.ScanCatchUp);

        var hint = JournalHintBuilder.BuildHint(loss);

        Assert.That(hint, Does.StartWith("Hint on D:"));
        Assert.That(hint, Does.Contain("while the scan ran, so the scan could not catch up and the drive was scanned again"));
        Assert.That(hint, Does.Contain("a journal of at least 256 MB would have avoided this rescan"));
        Assert.That(hint, Does.Not.Contain("full rescan was needed"));
        Assert.That(hint, Does.Not.Contain("Live updates"));
    }

    [Test]
    public void ScanCatchUpCheckpointTrimmed_WithoutSize_OffersNoSize()
    {
        var loss = FakeJournalSettingsSource.TrimmedLoss('E', 8L * 1024 * 1024, 0,
            detectedDuring: JournalCheckpointLossDetection.ScanCatchUp) with
        { SizeThatWouldHaveRetained = null };

        var hint = JournalHintBuilder.BuildHint(loss);

        Assert.That(hint, Does.Contain("the drive was scanned again"));
        Assert.That(hint, Does.Contain("too large to represent"));
        Assert.That(hint, Does.Not.Contain("increase the journal size"));
    }

    [Test]
    public void ScanCatchUpJournalRecreated_ExplainsTheRepeatedScanWithNoSizeOffered()
    {
        var loss = FakeJournalSettingsSource.RecreatedLoss('C', 64L * 1024 * 1024,
            detectedDuring: JournalCheckpointLossDetection.ScanCatchUp);

        var hint = JournalHintBuilder.BuildHint(loss);

        Assert.That(hint, Does.StartWith("Hint on C:"));
        Assert.That(hint, Does.Contain("recreated while the drive was being scanned"));
        Assert.That(hint, Does.Contain("Nothing needs changing."));
        Assert.That(hint, Does.Not.Contain("increase the journal size"));
    }
}
