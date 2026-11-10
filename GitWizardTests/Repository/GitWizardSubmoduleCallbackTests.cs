using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// Verifies <c>Refresh</c> swallows a handler that throws from the worktree-creation and submodule-creation
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
        var uninitializedCalls = 0;
        var handler = new RecordingUpdateHandler
        {
            CreatingUninitializedSubmodule = (_, _) =>
            {
                uninitializedCalls++;
                throw new InvalidOperationException("uninitialized-submodule failure");
            }
        };

        Assert.DoesNotThrow(() => repo.Refresh(handler));
        Assert.That(uninitializedCalls, Is.GreaterThan(0),
            "The uninitialized-submodule callback must fire (and its throw must be swallowed).");
    }

    [Test]
    public void Refresh_SwallowsThrowingSubmoduleCreatedCallback()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddInitializedSubmodule("sub");
        var repo = new GitWizardRepository(fixture.Path);
        var submoduleCreatedCalls = 0;
        var handler = new RecordingUpdateHandler
        {
            CreatingSubmodule = (_, _) =>
            {
                submoduleCreatedCalls++;
                throw new InvalidOperationException("submodule-created failure");
            }
        };

        Assert.DoesNotThrow(() => repo.Refresh(handler));
        Assert.That(submoduleCreatedCalls, Is.GreaterThan(0),
            "The submodule-created callback must fire (and its throw must be swallowed).");
    }

    [Test]
    public void Refresh_SwallowsThrowingWorktreeCreatedCallback()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddWorktree("feature-worktree");
        var repo = new GitWizardRepository(fixture.Path);
        var worktreeCreatedCalls = 0;
        var handler = new RecordingUpdateHandler
        {
            CreatingWorktree = _ =>
            {
                worktreeCreatedCalls++;
                throw new InvalidOperationException("worktree-created failure");
            }
        };

        Assert.DoesNotThrow(() => repo.Refresh(handler));
        Assert.That(worktreeCreatedCalls, Is.GreaterThan(0),
            "The worktree-created callback must fire (and its throw must be swallowed).");
    }
}
