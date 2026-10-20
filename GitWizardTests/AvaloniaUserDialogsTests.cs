using GitWizardUI.Services;

namespace GitWizardTests;

public class AvaloniaUserDialogsTests
{
    [Test]
    public async Task ShowAlertAsync_NoDesktopOwner_CompletesWithoutThrowing()
    {
        var dialogs = new AvaloniaUserDialogs();
        await dialogs.ShowAlertAsync("Title", "Message");
        Assert.Pass();
    }

    [Test]
    public async Task ConfirmAsync_NoDesktopOwner_ReturnsFalse()
    {
        var dialogs = new AvaloniaUserDialogs();
        var result = await dialogs.ConfirmAsync("Title", "Message");
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task ConfirmAsync_CustomLabels_NoDesktopOwner_ReturnsFalse()
    {
        var dialogs = new AvaloniaUserDialogs();
        var result = await dialogs.ConfirmAsync("Title", "Message", "Accept", "Cancel");
        Assert.That(result, Is.False);
    }
}
