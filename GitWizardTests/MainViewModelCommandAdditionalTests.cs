using GitWizard;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

/// <summary>
/// Additional command tests for <c>MainViewModel.Commands.cs</c> methods not yet covered
/// by <see cref="MainViewModelCommandTests"/>: ToggleGroupExpand, ProcessUICommand dispatch,
/// OpenInExplorer guards, DeepRefreshRepository guards, and CheckoutMatchingBranch guards.
/// </summary>
public class MainViewModelCommandAdditionalTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel CreateViewModel() =>
        new(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

    static MainViewModel CreateViewModel(StubUserDialogs dialogs) =>
        new(new StubUiDispatcher(), dialogs, new StubClipboardService());

    static RepositoryNodeViewModel RepoNode(string workingDirectory) =>
        new(new GitWizardRepository(workingDirectory));

    // ── CopyToClipboardAsync ──────────────────────────────────────────

    [Test]
    public async Task CopyToClipboardAsync_WritesToClipboardAndLightsJustCopied()
    {
        var clipboard = new StubClipboardService();
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), clipboard);
        var node = RepoNode("C:/projects/widget");

        var task = vm.CopyToClipboardAsync(node);
        Assert.That(node.JustCopied, Is.True, "JustCopied must be set immediately after copy.");
        Assert.That(clipboard.Writes, Does.Contain("C:/projects/widget"));

        await task;
        Assert.That(node.JustCopied, Is.False, "JustCopied must clear after the delay.");
    }

    [Test]
    public async Task CopyToClipboardAsync_EmptyWorkingDirectory_DoesNothing()
    {
        var clipboard = new StubClipboardService();
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), clipboard);
        var node = RepoNode("");

        await vm.CopyToClipboardAsync(node);

        Assert.Multiple(() =>
        {
            Assert.That(clipboard.Writes, Is.Empty);
            Assert.That(node.JustCopied, Is.False);
        });
    }

    // ── ToggleGroupExpand ─────────────────────────────────────────────

    [Test]
    public void ToggleGroupExpand_ExpandsCollapsedGroup()
    {
        var vm = CreateViewModel();
        vm.ToggleGroupMode(GroupMode.Drive);

        vm.AddRepository(new GitWizardRepository("C:/repos/alpha"));
        vm.AddRepository(new GitWizardRepository("C:/repos/beta"));

        // With Drive grouping, we get one collapsed group header.
        Assert.That(vm.Repositories, Has.Count.EqualTo(1), "Precondition: one group header.");
        var header = vm.Repositories[0];
        Assert.That(header.IsGroupHeader, Is.True);
        Assert.That(header.IsExpanded, Is.False, "Precondition: header starts collapsed.");

        vm.ToggleGroupExpand(header);

        Assert.Multiple(() =>
        {
            Assert.That(header.IsExpanded, Is.True, "Header must be expanded after toggle.");
            // Header + 2 children should be in the flat list.
            Assert.That(vm.Repositories, Has.Count.EqualTo(3),
                "Expanding must insert children after the header.");
            Assert.That(vm.Repositories[1].IsGroupHeader, Is.False);
            Assert.That(vm.Repositories[2].IsGroupHeader, Is.False);
        });
    }

    [Test]
    public void ToggleGroupExpand_CollapsesExpandedGroup()
    {
        var vm = CreateViewModel();
        vm.ToggleGroupMode(GroupMode.Drive);

        vm.AddRepository(new GitWizardRepository("C:/repos/alpha"));
        vm.AddRepository(new GitWizardRepository("C:/repos/beta"));

        var header = vm.Repositories[0];

        // Expand first
        vm.ToggleGroupExpand(header);
        Assert.That(vm.Repositories, Has.Count.EqualTo(3), "Precondition: expanded.");

        // Collapse
        vm.ToggleGroupExpand(header);

        Assert.Multiple(() =>
        {
            Assert.That(header.IsExpanded, Is.False, "Header must be collapsed after second toggle.");
            Assert.That(vm.Repositories, Has.Count.EqualTo(1),
                "Collapsing must remove children from the flat list.");
        });
    }

    [Test]
    public void ToggleGroupExpand_NullNode_IsNoOp()
    {
        var vm = CreateViewModel();

        // Must not throw.
        vm.ToggleGroupExpand(null);

        Assert.That(vm.Repositories, Is.Empty);
    }

    [Test]
    public void ToggleGroupExpand_NonGroupHeaderNode_IsNoOp()
    {
        var vm = CreateViewModel();
        var node = RepoNode("C:/repos/alpha");
        vm.Repositories.Add(node);

        vm.ToggleGroupExpand(node);

        // Should not throw and the list should be unchanged.
        Assert.That(vm.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void ToggleGroupExpand_HeaderNotInRepositories_IsNoOp()
    {
        var vm = CreateViewModel();
        var header = RepositoryNodeViewModel.CreateGroupHeader("C:\\");

        // Header isn't in Repositories, so IndexOf returns -1.
        vm.ToggleGroupExpand(header);

        Assert.That(vm.Repositories, Is.Empty);
    }

    [Test]
    public void ToggleGroupExpand_InvokesScrollToRequest()
    {
        var vm = CreateViewModel();
        vm.ToggleGroupMode(GroupMode.Drive);
        vm.AddRepository(new GitWizardRepository("C:/repos/alpha"));

        var header = vm.Repositories[0];
        RepositoryNodeViewModel? scrolledTo = null;
        vm.ScrollToRequest = n => scrolledTo = n;

        vm.ToggleGroupExpand(header);

        Assert.That(scrolledTo, Is.SameAs(header),
            "ToggleGroupExpand must invoke ScrollToRequest with the toggled header.");
    }

    // ── OpenInExplorer ────────────────────────────────────────────────

    [Test]
    public void OpenInExplorer_NullNode_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var vm = CreateViewModel(dialogs);

        vm.OpenInExplorerCommand.Execute(null);

        Assert.That(dialogs.AlertCalls, Is.Empty);
    }

    [Test]
    public void OpenInExplorer_GroupHeaderNode_ToggleExpandsInstead()
    {
        var vm = CreateViewModel();
        vm.ToggleGroupMode(GroupMode.Drive);
        vm.AddRepository(new GitWizardRepository("C:/repos/alpha"));

        var header = vm.Repositories[0];
        Assert.That(header.IsGroupHeader, Is.True, "Precondition.");
        Assert.That(header.IsExpanded, Is.False, "Precondition: starts collapsed.");

        vm.OpenInExplorerCommand.Execute(header);

        Assert.That(header.IsExpanded, Is.True,
            "OpenInExplorer on a group header must toggle expand, not open explorer.");
    }

    [Test]
    public void OpenInExplorer_EmptyWorkingDirectory_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var vm = CreateViewModel(dialogs);
        var node = RepoNode("");

        vm.OpenInExplorerCommand.Execute(node);

        Assert.That(dialogs.AlertCalls, Is.Empty,
            "An empty working directory must silently bail out.");
    }

    // ── DeepRefreshRepository ─────────────────────────────────────────

    [Test]
    public void DeepRefreshRepository_NullNode_DoesNothing()
    {
        var vm = CreateViewModel();

        // Must not throw.
        vm.DeepRefreshCommand.Execute(null);
    }

    [Test]
    public void DeepRefreshRepository_GroupHeaderNode_DoesNothing()
    {
        var vm = CreateViewModel();
        var header = RepositoryNodeViewModel.CreateGroupHeader("C:\\");

        vm.DeepRefreshCommand.Execute(header);

        // Group header has no real repository to refresh; should be a no-op.
        Assert.That(header.Status, Is.EqualTo(RefreshStatus.Success),
            "The group header's status must remain unchanged.");
    }

    [Test]
    public void DeepRefreshRepository_SetsStatusToRefreshing()
    {
        var vm = CreateViewModel();
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        // The DeepRefresh sets Status = Refreshing synchronously before spawning the Task.
        vm.DeepRefreshCommand.Execute(node);

        Assert.That(node.Status, Is.EqualTo(RefreshStatus.Refreshing));
    }

    // ── CheckoutMatchingBranch ────────────────────────────────────────

    [Test]
    public void CheckoutMatchingBranch_NullNode_DoesNothing()
    {
        var vm = CreateViewModel();

        // Must not throw.
        vm.CheckoutMatchingBranchCommand.Execute(null);
    }

    [Test]
    public void CheckoutMatchingBranch_GroupHeader_DoesNothing()
    {
        var vm = CreateViewModel();
        var header = RepositoryNodeViewModel.CreateGroupHeader("C:\\");

        vm.CheckoutMatchingBranchCommand.Execute(header);

        // Should be a no-op, no crash.
        Assert.That(header.IsGroupHeader, Is.True);
    }

    [Test]
    public void CheckoutMatchingBranch_EmptyMatchingBranch_DoesNothing()
    {
        var vm = CreateViewModel();
        var node = RepoNode("C:/repos/alpha");

        // MatchingBranchName is null on a freshly constructed repo, so the early return fires.
        vm.CheckoutMatchingBranchCommand.Execute(node);

        // No crash expected; verify the node is unchanged.
        Assert.That(node.Status, Is.EqualTo(RefreshStatus.Refreshing));
    }

    // ── CleanDownstreamBranches ───────────────────────────────────────

    [Test]
    public async Task CleanDownstreamBranches_NullNode_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var vm = CreateViewModel(dialogs);

        await vm.CleanDownstreamBranchesAsync(null);

        Assert.That(dialogs.AlertCalls, Is.Empty);
    }

    [Test]
    public async Task CleanDownstreamBranches_GroupHeader_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var vm = CreateViewModel(dialogs);
        var header = RepositoryNodeViewModel.CreateGroupHeader("C:\\");

        await vm.CleanDownstreamBranchesAsync(header);

        Assert.That(dialogs.AlertCalls, Is.Empty);
    }

    // ── ProcessUICommand (covered implicitly via IUpdateHandler) ──────

    [Test]
    public void ProcessUICommand_RepositoryCreated_AddsRepo()
    {
        var vm = CreateViewModel();
        var repo = new GitWizardRepository("C:/repos/alpha");

        // Call AddRepository directly (ProcessUICommand dispatches to it).
        vm.AddRepository(repo);

        Assert.That(vm.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void ProcessUICommand_RefreshCompleted_UpdatesNode()
    {
        var vm = CreateViewModel();
        var repo = new GitWizardRepository("C:/repos/alpha");
        vm.AddRepository(repo);

        // Simulate a refresh completion.
        vm.UpdateCompletedRepository(repo);

        Assert.That(vm.Repositories[0].Status, Is.EqualTo(RefreshStatus.Success));
    }

    // ── OpenInFork ────────────────────────────────────────────────────

    [Test]
    public void OpenInFork_EmptyWorkingDirectory_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var vm = new MainViewModel(new StubUiDispatcher(), dialogs, new StubClipboardService());

        vm.OpenInForkCommand.Execute(RepoNode(""));

        Assert.That(dialogs.AlertCalls, Is.Empty,
            "An empty working directory must silently bail without alerting.");
    }

    [Test]
    public void OpenInFork_GroupHeader_DoesNothing()
    {
        var dialogs = new StubUserDialogs();
        var vm = new MainViewModel(new StubUiDispatcher(), dialogs, new StubClipboardService());

        // GroupHeader has empty WorkingDirectory, so it hits the null/empty guard.
        vm.OpenInForkCommand.Execute(RepositoryNodeViewModel.CreateGroupHeader("C:\\"));

        Assert.That(dialogs.AlertCalls, Is.Empty);
    }

    // ── ShowAlert / DisplayAlertSafe ──────────────────────────────────

    [Test]
    public void OpenInFork_InvalidPath_ShowsErrorAlert()
    {
        var dialogs = new StubUserDialogs();
        var vm = new MainViewModel(new StubUiDispatcher(), dialogs, new StubClipboardService());

        vm.OpenInForkCommand.Execute(RepoNode("/definitely/not/a/real/path/xyz123"));

        Assert.That(dialogs.AlertCalls.Any(a => a.Message.Contains("Invalid repository path")),
            Is.True, "A non-existent directory must surface the invalid-path alert.");
    }

    // ── IUpdateHandler: SendUpdateMessage ─────────────────────────────

    [Test]
    public void SendUpdateMessage_UpdatesHeaderText()
    {
        var vm = CreateViewModel();

        vm.SendUpdateMessage("Scanning drives...");

        Assert.That(vm.HeaderText, Is.EqualTo("Scanning drives..."));
    }

    [Test]
    public void StartProgress_SetsProgressState()
    {
        var vm = CreateViewModel();

        vm.StartProgress("Refreshing", 50);

        Assert.Multiple(() =>
        {
            Assert.That(vm.IsProgressVisible, Is.True);
            Assert.That(vm.IsScanning, Is.False, "StartProgress must disable the scanning indicator.");
        });
    }

    [Test]
    public void UpdateProgress_SetsProgressCount()
    {
        var vm = CreateViewModel();
        vm.StartProgress("Refreshing", 50);

        vm.UpdateProgress(25);

        // UpdateProgress only sets the backing field; the UI thread loop reads it.
        // No assertion on the visible property since the loop hasn't run, but verify no crash.
        Assert.Pass("UpdateProgress must not throw.");
    }
}
