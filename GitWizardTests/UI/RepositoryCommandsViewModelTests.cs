using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

/// <summary>
/// The failure and alert paths of the per-row commands (Fork launch, checkout of the matching branch,
/// a failing alert dialog) as the user sees them: an alert and an error-log entry.
/// </summary>
[TestFixture]
[NonParallelizable]
public class RepositoryCommandsViewModelTests
{
    sealed class ThrowingDialogs : IUserDialogs
    {
        public Task ShowAlertAsync(string title, string message, string okLabel = "OK")
            => throw new InvalidOperationException("dialog host is gone");

        public Task<bool> ConfirmAsync(string title, string message, string acceptLabel = "Yes", string cancelLabel = "No")
            => throw new InvalidOperationException("dialog host is gone");
    }

    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static void ConfigureForkPath(string forkPath)
    {
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        configuration.ForkPath = forkPath;
        GitWizardConfiguration.SaveGlobalConfiguration(configuration);
    }

    [Test]
    public async Task OpenInFork_DirectoryMissing_ShowsInvalidPathAlert()
    {
        var dialogs = new StubUserDialogs();
        await using var viewModel = TestViewModels.Create(dialogs: dialogs);
        using var missing = new TempDirectory(create: false);

        await viewModel.Commands.OpenInForkAsync(new RepositoryNodeViewModel(new GitWizardRepository(missing.Path)));

        Assert.That(dialogs.AlertCalls.Single(), Is.EqualTo(new StubUserDialogs.AlertCall("Error", "Invalid repository path")));
    }

    [Test]
    public async Task OpenInFork_ConfiguredForkMissing_NamesTheConfiguredPath()
    {
        using var sandbox = new LocalFilesSandbox();
        using var repositoryDirectory = new TempDirectory();
        var forkPath = Path.Combine(sandbox.Path, "missing", "Fork.exe");
        ConfigureForkPath(forkPath);
        var dialogs = new StubUserDialogs();
        await using var viewModel = TestViewModels.Create(dialogs: dialogs);

        await viewModel.Commands.OpenInForkAsync(new RepositoryNodeViewModel(new GitWizardRepository(repositoryDirectory.Path)));

        var alert = dialogs.AlertCalls.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(alert.Title, Is.EqualTo("Error"));
            Assert.That(alert.Message, Does.Contain(forkPath).And.Contain("Please ensure Fork is installed."));
        }
    }

    [Test]
    public async Task OpenInFork_ForkCannotBeLaunched_AlertsAndLogsTheFailure()
    {
        using var sandbox = new LocalFilesSandbox();
        using var repositoryDirectory = new TempDirectory();
        var notAProgram = Path.Combine(sandbox.Path, "Fork.exe");
        await File.WriteAllTextAsync(notAProgram, "this is not an executable");
        ConfigureForkPath(notAProgram);
        var dialogs = new StubUserDialogs();
        await using var viewModel = TestViewModels.Create(dialogs: dialogs);

        await viewModel.Commands.OpenInForkAsync(new RepositoryNodeViewModel(new GitWizardRepository(repositoryDirectory.Path)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dialogs.AlertCalls.Single().Message, Does.StartWith("Could not launch Fork: "));
            Assert.That(viewModel.ErrorLog.Entries.Single().Message, Does.StartWith("Could not launch Fork: "));
            Assert.That(viewModel.ErrorLog.Entries.Single().RepositoryPath, Is.EqualTo(repositoryDirectory.Path));
        }
    }

    [Test]
    public async Task AlertDialogFailure_IsRecordedInTheErrorLogInsteadOfPropagating()
    {
        await using var viewModel = TestViewModels.Create(dialogs: new ThrowingDialogs());
        using var missing = new TempDirectory(create: false);

        await viewModel.Commands.OpenInForkAsync(new RepositoryNodeViewModel(new GitWizardRepository(missing.Path)));

        Assert.That(viewModel.ErrorLog.Entries.Single().Message,
            Is.EqualTo("Failed to display alert dialog: dialog host is gone"));
    }

    [Test]
    public async Task CheckoutMatchingBranch_Succeeds_RefreshesTheRowWithoutLoggingAnError()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.DetachHead();
        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        var node = new RepositoryNodeViewModel(repository);
        var updated = 0;
        node.PropertyChanged += (_, _) => Interlocked.Increment(ref updated);
        await using var viewModel = TestViewModels.Create();

        viewModel.Commands.CheckoutMatchingBranch(node);

        await AsyncCondition.WaitUntilAsync(() => Volatile.Read(ref updated) > 0);
        Assert.That(viewModel.ErrorLog.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task CheckoutMatchingBranch_BranchDeletedMeanwhile_AlertsAndLogsTheFailure()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.DetachHead();
        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        var node = new RepositoryNodeViewModel(repository);
        var branchName = node.MatchingBranchName!;
        TempRepoFixture.RunGit(fixture.Path, "branch", "-D", branchName);
        var dialogs = new StubUserDialogs();
        await using var viewModel = TestViewModels.Create(dialogs: dialogs);

        viewModel.Commands.CheckoutMatchingBranch(node);

        await AsyncCondition.WaitUntilAsync(() => viewModel.ErrorLog.Count == 1 && dialogs.AlertCalls.Count == 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dialogs.AlertCalls.Single().Title, Is.EqualTo("Checkout Failed"));
            Assert.That(viewModel.ErrorLog.Entries.Single().Message, Does.StartWith($"Could not check out branch '{branchName}': "));
        }
    }
}
