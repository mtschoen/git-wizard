using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;
using MFTLibTestExtensions;

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

    [TestCase(64, true)]
    [TestCase(0, false)]
    public void GrowUnavailableReason_DependsOnAvailableTarget(long allocationDelta, bool available)
    {
        var warning = new JournalWarning
        {
            DriveLetter = "C",
            VolumeSerial = 1,
            Loss = FakeJournalSettingsSource.TrimmedLoss('C', 30L * 1024 * 1024, 60L * 1024 * 1024, allocationDelta)
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.HasGrowAction, Is.EqualTo(available));
            Assert.That(warning.GrowUnavailableReason, available ? Is.Null : Is.EqualTo("The recorded journal settings do not provide a valid growth target."));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.HasGrowAction, Is.False);
            Assert.That(warning.GrowUnavailableReason, Does.Contain("journal was recreated"));
        }
    }

    [Test]
    public void GrowUnavailableReason_JournalAdvancedCause_ExplainsNoSizeWouldHaveHelped()
    {
        var warning = new JournalWarning
        {
            DriveLetter = "F",
            VolumeSerial = 1,
            Loss = SyntheticNotifications.CreateJournalCheckpointLoss('F', JournalCheckpointLossCause.JournalAdvanced, JournalCheckpointLossDetection.DriveOpening, new UsnJournalSettings { MaximumSize = 8L * 1024 * 1024, AllocationDelta = 64 })
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.HasGrowAction, Is.False);
            Assert.That(warning.GrowUnavailableReason, Is.EqualTo("The journal moved while the index was closed, so no journal size would have kept the saved position."));
        }
    }

    [Test]
    public void GrowUnavailableReason_TrimmedWithoutSize_ExplainsSizeTooLarge()
    {
        var warning = new JournalWarning
        {
            DriveLetter = "E",
            VolumeSerial = 1,
            Loss = SyntheticNotifications.CreateJournalCheckpointLoss('E', JournalCheckpointLossCause.CheckpointTrimmed, JournalCheckpointLossDetection.DriveOpening, new UsnJournalSettings { MaximumSize = 8L * 1024 * 1024, AllocationDelta = 64 }) with { BytesBehind = 4_000_000, SizeThatWouldHaveRetained = null }
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.HasGrowAction, Is.False);
            Assert.That(warning.GrowUnavailableReason, Does.Contain("too large to represent"));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.HasGrowAction, Is.False);
            Assert.That(warning.GrowUnavailableReason, Does.Contain("already at least as large"));
        }
    }
}
