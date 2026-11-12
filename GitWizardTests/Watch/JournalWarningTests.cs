using GitWizard;
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
            Loss = SyntheticNotifications.CreateJournalCheckpointLoss('F', JournalCheckpointLossCause.JournalAdvanced, JournalCheckpointLossDetection.RootOpening, new UsnJournalSettings { MaximumSize = 8L * 1024 * 1024, AllocationDelta = 64 })
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
            Loss = SyntheticNotifications.CreateJournalCheckpointLoss('E', JournalCheckpointLossCause.CheckpointTrimmed, JournalCheckpointLossDetection.RootOpening, new UsnJournalSettings { MaximumSize = 8L * 1024 * 1024, AllocationDelta = 64 }) with { BytesBehind = 4_000_000, SizeThatWouldHaveRetained = null }
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

    [Test]
    public void AboveMaximumRecommended_AtCapDisablesDefaultGrowButKeepsOverrideAction()
    {
        const long eightGigabytes = 8L * 1024 * 1024 * 1024;
        const long tenGigabytes = 10L * 1024 * 1024 * 1024;
        var belowCap = new JournalWarning
        {
            DriveLetter = "C",
            VolumeSerial = 1,
            Loss = FakeJournalSettingsSource.TrimmedLoss('C', 32L * 1024 * 1024, tenGigabytes),
            MaximumRecommendedSize = eightGigabytes
        };
        var atCap = belowCap with
        {
            CurrentMaximumSize = eightGigabytes
        };
        var atTarget = belowCap with
        {
            CurrentMaximumSize = tenGigabytes
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(belowCap.HasGrowAction, Is.True);
            Assert.That(belowCap.CanGrowAboveMaximumRecommended, Is.True);
            Assert.That(belowCap.GrowUnavailableReason, Is.Null);
            Assert.That(atCap.HasGrowAction, Is.False);
            Assert.That(atCap.CanGrowAboveMaximumRecommended, Is.True);
            Assert.That(atCap.GrowUnavailableReason, Does.Contain("recommended maximum of 8 GB").And.Contain("needed 10 GB"));
            Assert.That(atCap.Message, Does.Contain("it was 32 MB when this was detected").And.Contain("applied 8 GB"));
            Assert.That(atTarget.HasGrowAction, Is.False);
            Assert.That(atTarget.CanGrowAboveMaximumRecommended, Is.False);
            Assert.That(atTarget.GrowUnavailableReason, Does.Contain("already at least as large as the size that would have avoided the rescan"));
        }
    }

    [Test]
    public void UnchangedOutcome_DisablesBothGrowActionsWithKeptSizeReasonAndOmitsAppliedClause()
    {
        const long eightGigabytes = 8L * 1024 * 1024 * 1024;
        const long tenGigabytes = 10L * 1024 * 1024 * 1024;
        var warning = new JournalWarning
        {
            DriveLetter = "C",
            VolumeSerial = 1,
            Loss = FakeJournalSettingsSource.TrimmedLoss('C', 2L * 1024 * 1024, tenGigabytes),
            MaximumRecommendedSize = eightGigabytes,
            CurrentMaximumSize = 4L * 1024 * 1024,
            LastGrowthOutcome = UsnJournalGrowthOutcome.Unchanged
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.HasGrowAction, Is.False);
            Assert.That(warning.CanGrowAboveMaximumRecommended, Is.False);
            Assert.That(warning.GrowUnavailableReason, Is.EqualTo("Windows accepted the request but kept the journal at 4 MB."));
            Assert.That(warning.Message, Does.Not.Contain("applied"));
        }
    }

    [Test]
    public void GrewAsRequestedWithNtfsRoundedDownReadBack_DisablesGrowAndShowsReadBackSizeAsApplied()
    {
        const long eightGigabytes = 8L * 1024 * 1024 * 1024;
        const long requestedBytes = 214_643_302L;
        const long readBackBytes = 214_433_792L;
        var warning = new JournalWarning
        {
            DriveLetter = "C",
            VolumeSerial = 1,
            Loss = FakeJournalSettingsSource.TrimmedLoss('C', 32L * 1024 * 1024, requestedBytes),
            MaximumRecommendedSize = eightGigabytes,
            CurrentMaximumSize = readBackBytes,
            LastGrowthOutcome = UsnJournalGrowthOutcome.GrewAsRequested
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.HasGrowAction, Is.False);
            Assert.That(warning.CanGrowAboveMaximumRecommended, Is.False);
            Assert.That(warning.GrowUnavailableReason, Is.EqualTo("The journal is already at least as large as the size that would have avoided the rescan."));
            Assert.That(warning.Message, Does.Contain($"applied {ByteSizeFormatter.Format(readBackBytes)}"));
        }
    }
}
