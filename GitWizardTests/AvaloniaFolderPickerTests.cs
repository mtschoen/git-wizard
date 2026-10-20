using GitWizardUI.Services;

namespace GitWizardTests;

public class AvaloniaFolderPickerTests
{
    [Test]
    public async Task PickFolderAsync_NoDesktopOwner_ReturnsNull()
    {
        var picker = new AvaloniaFolderPicker();
        var result = await picker.PickFolderAsync();
        Assert.That(result, Is.Null);
    }
}
