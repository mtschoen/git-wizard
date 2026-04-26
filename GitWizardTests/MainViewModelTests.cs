using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

public class MainViewModelTests
{
    [Test]
    public void Construction_RequiresInjectedServices()
    {
        var dispatcher = new StubUiDispatcher();
        var dialogs = new StubUserDialogs();

        var vm = new MainViewModel(dispatcher, dialogs);

        Assert.That(vm, Is.Not.Null);
        Assert.That(vm.HeaderText, Is.EqualTo("GitWizard"));
    }
}
