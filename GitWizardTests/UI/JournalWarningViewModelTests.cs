using GitWizard.Watch;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests.UI;

[TestFixture]
public class JournalWarningViewModelTests
{
    [Test]
    public void Construction_PropertiesMapFromWarning()
    {
        var loss = FakeJournalSettingsSource.TrimmedLoss('C', 30L * 1024 * 1024, 60L * 1024 * 1024, 10L * 1024 * 1024);
        var warning = new JournalWarning { DriveLetter = "C", VolumeSerial = 12345, Loss = loss };
        var viewModel = new JournalWarningViewModel(warning);

        Assert.That(viewModel.DriveLetter, Is.EqualTo("C"));
        Assert.That(viewModel.HasGrowAction, Is.True);
        Assert.That(viewModel.StatusMessage, Is.Empty);
        Assert.That(viewModel.Warning.Message, Does.Contain("Hint on C:"));
    }

    [Test]
    public void RecreatedCause_HasNoGrowAction()
    {
        var loss = FakeJournalSettingsSource.RecreatedLoss('D', 64L * 1024 * 1024);
        var warning = new JournalWarning { DriveLetter = "D", VolumeSerial = 67890, Loss = loss };
        var viewModel = new JournalWarningViewModel(warning);

        Assert.That(viewModel.HasGrowAction, Is.False);
        Assert.That(viewModel.Warning.Message, Does.Contain("journal was recreated"));
        Assert.That(viewModel.Warning.Message, Does.Not.Contain("increase the journal size"));
    }

    [Test]
    public void GrowUnavailableReason_FollowsWarning()
    {
        var growable = new JournalWarningViewModel(new JournalWarning
        {
            DriveLetter = "C",
            VolumeSerial = 1,
            Loss = FakeJournalSettingsSource.TrimmedLoss('C', 30L * 1024 * 1024, 60L * 1024 * 1024, 10L * 1024 * 1024)
        });
        Assert.That(growable.GrowUnavailableReason, Is.Null);

        var recreated = new JournalWarningViewModel(new JournalWarning
        {
            DriveLetter = "D",
            VolumeSerial = 2,
            Loss = FakeJournalSettingsSource.RecreatedLoss('D', 64L * 1024 * 1024)
        });
        Assert.That(recreated.GrowUnavailableReason, Does.Contain("journal was recreated"));

        var changes = new List<string?>();
        recreated.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        recreated.ApplyLoss(recreated.Warning with
        {
            Loss = FakeJournalSettingsSource.TrimmedLoss('D', 30L * 1024 * 1024, 60L * 1024 * 1024, 10L * 1024 * 1024)
        });
        Assert.That(recreated.GrowUnavailableReason, Is.Null);
        Assert.That(changes, Does.Contain(nameof(JournalWarningViewModel.GrowUnavailableReason)));
    }
}
