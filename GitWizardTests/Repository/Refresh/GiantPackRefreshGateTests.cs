using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

public class GiantPackRefreshGateTests
{
    const long OneHundredMebibytes = 100L * 1024 * 1024;

    [Test]
    public void Refresh_PackIndexesOver100MiB_DoNotOverlap_Issue451()
    {
        Assert.That(RunTwoRepositoryRefresh(OneHundredMebibytes + 1), Is.EqualTo(1),
            "Two repositories with pack indexes over 100 MiB entered refresh together.");
    }

    [Test]
    public void Refresh_PackIndexesAt100MiB_CanOverlap_Issue451()
    {
        Assert.That(RunTwoRepositoryRefresh(OneHundredMebibytes), Is.EqualTo(2),
            "The strict over-100 MiB threshold must not serialize an index exactly at 100 MiB.");
    }

    [Test]
    public void Refresh_LinkedWorktreesSharingGiantPack_DoNotOverlap_Issue451()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var first = fixture.AddWorktree("giant-gate-one");
        var second = fixture.AddWorktree("giant-gate-two");
        PackIndexFixture.Create(fixture.Path, OneHundredMebibytes + 1);

        using var probe = new RefreshOverlapProbe();
        var report = new GitWizardReport
        {
            Repositories =
            {
                [first] = new GitWizardRepository(first) { IsWorktree = true, PhaseObserverForTesting = probe.OnPhase },
                [second] = new GitWizardRepository(second) { IsWorktree = true, PhaseObserverForTesting = probe.OnPhase },
            },
        };

        report.Refresh([first, second]);

        Assert.That(probe.MaximumActive, Is.EqualTo(1),
            "Linked worktrees bypassed the giant-pack gate through their gitfiles.");
    }

    [Test]
    public void Refresh_BareRepositoriesWithGiantPacks_DoNotOverlap_Issue451()
    {
        GitWizardLog.SilentMode = true;
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        Repository.Init(first.Path, isBare: true);
        Repository.Init(second.Path, isBare: true);
        PackIndexFixture.CreateBare(first.Path, OneHundredMebibytes + 1);
        PackIndexFixture.CreateBare(second.Path, OneHundredMebibytes + 1);

        using var probe = new RefreshOverlapProbe();
        var report = new GitWizardReport
        {
            Repositories =
            {
                [first.Path] = new GitWizardRepository(first.Path) { PhaseObserverForTesting = probe.OnPhase },
                [second.Path] = new GitWizardRepository(second.Path) { PhaseObserverForTesting = probe.OnPhase },
            },
        };

        report.Refresh([first.Path, second.Path]);

        Assert.That(probe.MaximumActive, Is.EqualTo(1),
            "Bare repositories bypassed the giant-pack gate without a .git directory.");
    }

    [Test]
    public async Task Acquire_WaitingForGiantPack_ObservesCancellation_Issue451()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        PackIndexFixture.Create(fixture.Path, OneHundredMebibytes + 1);
        using var heldGate = GiantPackRefreshGate.Acquire(fixture.Path, CancellationToken.None);
        using var cancellationTokenSource = new CancellationTokenSource();
        var attemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixturePath = fixture.Path;
        var cancellationToken = cancellationTokenSource.Token;

        var acquisition = Task.Run(() =>
        {
            attemptStarted.SetResult();
            Assert.Throws<OperationCanceledException>(() =>
                GiantPackRefreshGate.Acquire(fixturePath, cancellationToken));
        });

        await attemptStarted.Task.WaitAsync(TestTimeouts.ShortHangGuard);
        await cancellationTokenSource.CancelAsync();
        await acquisition.WaitAsync(TestTimeouts.ShortHangGuard);
    }

    static int RunTwoRepositoryRefresh(long packIndexBytes)
    {
        GitWizardLog.SilentMode = true;
        using var first = TempRepoFixture.CreateWithInitialCommit();
        using var second = TempRepoFixture.CreateWithInitialCommit();
        PackIndexFixture.Create(first.Path, packIndexBytes);
        PackIndexFixture.Create(second.Path, packIndexBytes);

        using var probe = new RefreshOverlapProbe();
        var report = new GitWizardReport
        {
            Repositories =
            {
                [first.Path] = new GitWizardRepository(first.Path) { PhaseObserverForTesting = probe.OnPhase },
                [second.Path] = new GitWizardRepository(second.Path) { PhaseObserverForTesting = probe.OnPhase },
            },
        };

        report.Refresh([first.Path, second.Path]);
        return probe.MaximumActive;
    }

}
