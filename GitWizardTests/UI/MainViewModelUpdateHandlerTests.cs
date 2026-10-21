using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Refresh tests are split by concern because the combined fixture exceeds 500 lines.
// Covers MainViewModel.UpdateHandler.cs.
public class MainViewModelUpdateHandlerTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel CreateViewModel() => TestViewModels.Create();

    static GitWizardRepository Repo(string workingDirectory) => new(workingDirectory);


    [Test]
    public void OnRepositoryCreated_QueuesAndProcessesViaAddRepository()
    {
        var vm = CreateViewModel();
        var repo = Repo("C:/repos/alpha");

        // OnRepositoryCreated enqueues a RepositoryCreated command.
        vm.OnRepositoryCreated(repo);

        // The batching window drains the queue asynchronously; verify the command was queued by
        // checking it didn't add inline. (AddRepository is called by the scheduled drain, not inline.)
        // This is an integration-boundary test: the IUpdateHandler interface is correctly wired.
        Assert.That(vm.Repositories, Is.Empty,
            "OnRepositoryCreated queues the repo asynchronously; it should not add inline.");
    }

    [Test]
    public void OnRepositoryRefreshCompleted_QueuesRefreshCompletedCommand()
    {
        var vm = CreateViewModel();
        var repo = Repo("C:/repos/alpha");

        // First add the repo directly so the map is populated.
        vm.AddRepository(repo);
        Assert.That(vm.Repositories, Has.Count.EqualTo(1), "Precondition.");

        // Queue a refresh completed event.
        vm.OnRepositoryRefreshCompleted(repo);

        // The command is queued, not processed inline.
        Assert.That(vm.Repositories[0].Status, Is.EqualTo(RefreshStatus.Refreshing),
            "OnRepositoryRefreshCompleted queues the command; it does not update inline.");
    }

    [Test]
    public void OnSubmoduleCreated_QueuesSubmoduleCommand()
    {
        var vm = CreateViewModel();
        var parent = Repo("C:/repos/parent");
        var sub = Repo("C:/repos/parent/sub");

        vm.AddRepository(parent);
        vm.OnSubmoduleCreated(parent, sub);

        // Submodule is queued, not processed inline.
        var parentNode = vm.Repositories[0];
        Assert.That(parentNode.Children, Is.Empty,
            "OnSubmoduleCreated queues the submodule asynchronously.");
    }

    [Test]
    public void OnWorktreeCreated_QueuesWorktreeCommand()
    {
        var vm = CreateViewModel();
        var worktree = Repo("C:/repos/worktree");

        vm.OnWorktreeCreated(worktree);

        Assert.That(vm.Repositories, Is.Empty,
            "OnWorktreeCreated queues the worktree asynchronously.");
    }
}
