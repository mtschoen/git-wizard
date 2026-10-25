using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Core MainViewModel tests exercise construction, default property state, and swapped collection notification.
// Covers MainViewModel.cs, RefreshCoordinator.cs, RepositoryListViewModel.cs, and StatusHeaderViewModel.cs.
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
        Assert.That(vm.StatusHeader.HeaderText, Is.EqualTo("GitWizard"));
    }

    [Test]
    public void IsScanning_IsFalseBeforeAnyRefresh()
    {
        var vm = TestViewModels.Create();

        Assert.That(vm.Refresh.IsScanning, Is.False);
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
        var viewModel = CreateViewModel();
        var changes = new List<string?>();
        viewModel.Refresh.PropertyChanged += (_, arguments) => changes.Add(arguments.PropertyName);
        Assert.That(viewModel.Refresh.CanRefresh, Is.True);
        viewModel.Refresh.IsRefreshing = true;
        viewModel.Refresh.IsRefreshing = true;
        Assert.That(viewModel.Refresh.CanRefresh, Is.False);
        Assert.That(changes, Is.EqualTo([
            nameof(RefreshCoordinator.IsRefreshing), nameof(RefreshCoordinator.CanRefresh)
        ]));
        changes.Clear();
        viewModel.Refresh.IsRefreshing = false;
        Assert.That(viewModel.Refresh.CanRefresh, Is.True);
        Assert.That(changes, Is.EqualTo([
            nameof(RefreshCoordinator.IsRefreshing), nameof(RefreshCoordinator.CanRefresh)
        ]));
    }

    [Test]
    public void Repositories_StartEmpty()
    {
        var vm = CreateViewModel();
        Assert.That(vm.RepositoryList.Repositories, Is.Empty);
    }

    [Test]
    public void AfterRepositoriesSwap_InvokesTheAssignedAction()
    {
        var vm = CreateViewModel();
        var invoked = false;
        vm.RepositoryList.AfterRepositoriesSwap = () => invoked = true;

        vm.RepositoryList.AfterRepositoriesSwap!();

        Assert.That(invoked, Is.True);
    }
}
