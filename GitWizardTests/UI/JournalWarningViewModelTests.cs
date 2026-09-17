using GitWizard.Watch;
using GitWizardUI.ViewModels;

namespace GitWizardTests.UI;

[TestFixture]
public class JournalWarningViewModelTests
{
    [Test]
    public void Construction_PropertiesMapFromWarning()
    {
        var warning = new JournalWarning("C", 12345, 1024 * 1024 * 30, 1024 * 1024 * 60, 1024 * 1024 * 10);
        var viewModel = new JournalWarningViewModel(warning);

        Assert.That(viewModel.DriveLetter, Is.EqualTo("C"));
        Assert.That(viewModel.CurrentSizeText, Is.EqualTo("30 MB"));
        Assert.That(viewModel.RecommendedSizeText, Is.EqualTo("60 MB"));
        Assert.That(viewModel.StatusMessage, Is.Empty);
    }
}
