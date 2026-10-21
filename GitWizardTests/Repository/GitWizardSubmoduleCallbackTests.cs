using GitWizard;

namespace GitWizardTests;

/// <summary>
/// Verifies <c>RefreshSubmodules</c> swallows a handler that throws from the submodule-creation
/// callbacks (initialized and uninitialized), so one bad handler can't abort a scan.
/// </summary>
public class GitWizardSubmoduleCallbackTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void Refresh_SwallowsThrowingUninitializedSubmoduleCallback()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddUninitializedSubmodule("sub");
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new ThrowingSubmoduleHandler();

        Assert.DoesNotThrow(() => repo.Refresh(handler));
        Assert.That(handler.UninitializedCalls, Is.GreaterThan(0),
            "The uninitialized-submodule callback must fire (and its throw must be swallowed).");
    }

    [Test]
    public void Refresh_SwallowsThrowingSubmoduleCreatedCallback()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddInitializedSubmodule("sub");
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new ThrowingSubmoduleHandler();

        Assert.DoesNotThrow(() => repo.Refresh(handler));
        Assert.That(handler.SubmoduleCreatedCalls, Is.GreaterThan(0),
            "The submodule-created callback must fire (and its throw must be swallowed).");
    }

    sealed class ThrowingSubmoduleHandler : IUpdateHandler
    {
        public int SubmoduleCreatedCalls { get; private set; }
        public int UninitializedCalls { get; private set; }

        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) { }
        public void SendUpdateMessage(string? message) { }
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }

        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule)
        {
            SubmoduleCreatedCalls++;
            throw new InvalidOperationException("submodule-created failure");
        }

        public void OnWorktreeCreated(GitWizardRepository worktree) { }

        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath)
        {
            UninitializedCalls++;
            throw new InvalidOperationException("uninitialized-submodule failure");
        }

        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    }
}
