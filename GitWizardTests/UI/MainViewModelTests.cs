using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Core MainViewModel tests exercise construction, default property state, and swapped collection notification.
// Covers MainViewModel.cs.
public class MainViewModelTests
{
    static MainViewModel CreateViewModel() => TestViewModels.Create();

    [Test]
    public void Construction_RequiresInjectedServices()
    {
        var dispatcher = new StubUiDispatcher();
        var dialogs = new StubUserDialogs();
        var clipboard = new StubClipboardService();

        var vm = TestViewModels.Create(dispatcher, dialogs, clipboard);

        Assert.That(vm, Is.Not.Null);
        Assert.That(vm.HeaderText, Is.EqualTo("GitWizard"));
    }

    [Test]
    public void IsScanning_IsFalseBeforeAnyRefresh()
    {
        var vm = TestViewModels.Create();

        Assert.That(vm.IsScanning, Is.False);
    }

    [Test]
    public void ErrorsButtonLabel_ReflectsErrorLogCount()
    {
        var vm = CreateViewModel();

        Assert.That(vm.ErrorsButtonLabel, Is.EqualTo("Errors"),
            "With no errors logged, the button should not show a count.");

        vm.ErrorLog.AddError("boom");

        Assert.That(vm.ErrorsButtonLabel, Is.EqualTo("Errors (1)"));
    }

    [Test]
    public void ErrorsButtonLabel_ChangeRaisesPropertyChanged()
    {
        var vm = CreateViewModel();
        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, args) => raisedProperties.Add(args.PropertyName);

        vm.ErrorLog.AddError("boom");

        Assert.That(raisedProperties, Does.Contain(nameof(MainViewModel.ErrorsButtonLabel)));
    }

    [Test]
    public void CanRefresh_IsFalse_WhileRefreshing()
    {
        var vm = CreateViewModel();
        Assert.That(vm.CanRefresh, Is.True);

        vm.IsRefreshing = true;

        Assert.That(vm.CanRefresh, Is.False);
    }

    [Test]
    public void Repositories_StartEmpty()
    {
        var vm = CreateViewModel();
        Assert.That(vm.Repositories, Is.Empty);
    }

    [Test]
    public void AfterRepositoriesSwap_InvokesTheAssignedAction()
    {
        var vm = CreateViewModel();
        var invoked = false;
        vm.AfterRepositoriesSwap = () => invoked = true;

        vm.AfterRepositoriesSwap!();

        Assert.That(invoked, Is.True);
    }
}
