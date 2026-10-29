using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

public class NestedRefreshSchedulingTests
{
    [Test]
    public void Refresh_MultipleSubmodules_DoNotOverlap_Issue453()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddInitializedSubmodule("sub-one");
        fixture.AddInitializedSubmodule("sub-two");
        using var probe = new RefreshOverlapProbe();
        var onPhase = probe.OnPhase;
        var handler = new RecordingUpdateHandler
        {
            CreatingSubmodule = (_, submodule) => submodule.PhaseObserverForTesting = onPhase,
        };
        var repository = new GitWizardRepository(fixture.Path);

        repository.Refresh(handler);

        Assert.That(probe.MaximumActive, Is.EqualTo(1),
            "Nested submodule refreshes multiplied concurrency beyond the outer refresh worker.");
    }

    [Test]
    public void Refresh_MultipleWorktrees_DoNotOverlap_Issue453()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddWorktree("worktree-one");
        fixture.AddWorktree("worktree-two");
        using var probe = new RefreshOverlapProbe();
        var onPhase = probe.OnPhase;
        var handler = new RecordingUpdateHandler
        {
            CreatingWorktree = worktree => worktree.PhaseObserverForTesting = onPhase,
        };
        var repository = new GitWizardRepository(fixture.Path);

        repository.Refresh(handler);

        Assert.That(probe.MaximumActive, Is.EqualTo(1),
            "Nested worktree refreshes multiplied concurrency beyond the outer refresh worker.");
    }
}
