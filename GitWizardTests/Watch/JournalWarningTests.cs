using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests.Watch;

[TestFixture]
public class JournalWarningTests
{
    [Test]
    public void VolumeArmResult_Constructors_Defaults()
    {
        var result = new VolumeArmResult(new Dictionary<string, string>(), []);
        Assert.That(result.JournalWarnings, Is.Empty);
    }

    [Test]
    public void GrowUnavailableReason_WhenGrowAvailable_IsNull()
    {
        var warning = new JournalWarning
        {
            DriveLetter = "C",
            VolumeSerial = 1,
            Loss = FakeJournalSettingsSource.TrimmedLoss('C', 30L * 1024 * 1024, 60L * 1024 * 1024, 10L * 1024 * 1024)
        };

        Assert.That(warning.HasGrowAction, Is.True);
        Assert.That(warning.GrowUnavailableReason, Is.Null);
    }

    [Test]
    public void GrowUnavailableReason_RecreatedCause_ExplainsNoSizeWouldHaveHelped()
    {
        var warning = new JournalWarning
        {
            DriveLetter = "C",
            VolumeSerial = 1,
            Loss = FakeJournalSettingsSource.RecreatedLoss('C', 64L * 1024 * 1024)
        };

        Assert.That(warning.HasGrowAction, Is.False);
        Assert.That(warning.GrowUnavailableReason, Does.Contain("journal was recreated"));
    }

    [Test]
    public void GrowUnavailableReason_TrimmedWithoutSize_ExplainsSizeTooLarge()
    {
        var warning = new JournalWarning
        {
            DriveLetter = "E",
            VolumeSerial = 1,
            Loss = new JournalCheckpointLoss
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
            }
        };

        Assert.That(warning.HasGrowAction, Is.False);
        Assert.That(warning.GrowUnavailableReason, Does.Contain("too large to represent"));
    }

    [Test]
    public void GrowUnavailableReason_SuggestedSizeAtOrBelowCurrentMaximum_ExplainsJournalAlreadyLargeEnough()
    {
        var warning = new JournalWarning
        {
            DriveLetter = "C",
            VolumeSerial = 1,
            Loss = FakeJournalSettingsSource.TrimmedLoss('C', 128L * 1024 * 1024, 128L * 1024 * 1024)
        };

        Assert.That(warning.HasGrowAction, Is.False);
        Assert.That(warning.GrowUnavailableReason, Does.Contain("already at least as large"));
    }
}
