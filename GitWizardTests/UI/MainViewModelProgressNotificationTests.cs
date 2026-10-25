using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Progress notification tests exercise header text updates and progress bar visibility.
// Covers RefreshCoordinator.UiDrain.cs and StatusHeaderViewModel.cs.
public class MainViewModelProgressNotificationTests
{
    static MainViewModel CreateViewModel() => TestViewModels.Create();

    [Test]
    public async Task SendUpdateMessage_UpdatesHeaderText()
    {
        var dispatcher = new QueuedUiDispatcher();
        await using var viewModel = TestViewModels.Create(dispatcher: dispatcher);
        viewModel.Refresh.SendUpdateMessage("first");
        viewModel.Refresh.SendUpdateMessage("Scanning...");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dispatcher.QueuedCount, Is.EqualTo(1));
            Assert.That(viewModel.StatusHeader.HeaderText, Is.EqualTo("GitWizard"));
        }
        dispatcher.Drain();
        Assert.That(viewModel.StatusHeader.HeaderText, Is.EqualTo("Scanning..."));
        viewModel.Refresh.SendUpdateMessage("second burst");
        Assert.That(dispatcher.QueuedCount, Is.EqualTo(1));
        dispatcher.Drain();
        Assert.That(viewModel.StatusHeader.HeaderText, Is.EqualTo("second burst"));
    }

    [Test]
    public void SendUpdateMessage_Null_LeavesHeaderTextUnchanged()
    {
        var vm = CreateViewModel();
        vm.Refresh.SendUpdateMessage("original");

        vm.Refresh.SendUpdateMessage(null);

        Assert.That(vm.StatusHeader.HeaderText, Is.EqualTo("original"));
    }

    [Test]
    public void StartProgress_MakesProgressVisible()
    {
        var vm = CreateViewModel();
        vm.Refresh.IsScanning = true;

        vm.Refresh.StartProgress("scanning", 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.Refresh.IsProgressVisible, Is.True);
            Assert.That(vm.Refresh.ProgressText, Does.Contain("scanning"));
            Assert.That(vm.Refresh.IsScanning, Is.False, "StartProgress must disable the scanning indicator.");
        }
    }
}
