using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Progress notification tests exercise header text updates and progress bar visibility.
// Covers MainViewModel.UpdateHandler.cs.
public class MainViewModelProgressNotificationTests
{
    static MainViewModel CreateViewModel() =>
        new(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

    [Test]
    public void SendUpdateMessage_UpdatesHeaderText()
    {
        var vm = CreateViewModel();

        vm.SendUpdateMessage("Scanning...");

        Assert.That(vm.HeaderText, Is.EqualTo("Scanning..."));
    }

    [Test]
    public void SendUpdateMessage_Null_LeavesHeaderTextUnchanged()
    {
        var vm = CreateViewModel();
        vm.SendUpdateMessage("original");

        vm.SendUpdateMessage(null);

        Assert.That(vm.HeaderText, Is.EqualTo("original"));
    }

    [Test]
    public void StartProgress_MakesProgressVisible()
    {
        var vm = CreateViewModel();
        vm.IsScanning = true;

        vm.StartProgress("scanning", 10);

        Assert.That(vm.IsProgressVisible, Is.True);
        Assert.That(vm.ProgressText, Does.Contain("scanning"));
        Assert.That(vm.IsScanning, Is.False, "StartProgress must disable the scanning indicator.");
    }
}
