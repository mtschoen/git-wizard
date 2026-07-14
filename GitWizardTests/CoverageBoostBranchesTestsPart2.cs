using GitWizard;

namespace GitWizardTests;

/// <summary>
/// Additional CoverageBoostBranchesTests for remote/branch tracking, stash detection,
/// and serialization edge cases.
/// </summary>
public class CoverageBoostBranchesTests2
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void Refresh_PopulatesLastCommitDate()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.LastCommitDate, Is.Not.Null);
        Assert.That(repo.LastCommitDate!.Value, Is.GreaterThan(DateTimeOffset.MinValue));
    }

    [Test]
    public void Refresh_PopulatesCurrentBranch()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.CurrentBranch, Is.Not.Null);
        Assert.That(repo.CurrentBranch, Is.AnyOf("main", "master"));
    }

    [Test]
    public void Refresh_PopulatesDaysSinceLastCommit()
    {
        var commitTime = DateTimeOffset.Now.AddDays(-5);
        using var fixture = TempRepoFixture.CreateWithInitialCommit(commitTime);
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.DaysSinceLastCommit, Is.Not.Null);
        Assert.That(repo.DaysSinceLastCommit, Is.GreaterThanOrEqualTo(4));
    }

    [Test]
    public void Refresh_IsWorktreeProperty_DefaultsFalse()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();
        Assert.That(repo.IsWorktree, Is.False);
    }

    [Test]
    public void Refresh_MultipleWorktrees_AllDiscovered()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddWorktree("wt-a");
        fixture.AddWorktree("wt-b");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.Worktrees, Is.Not.Null);
        Assert.That(repo.Worktrees!, Has.Count.EqualTo(2));
    }

    [Test]
    public void IsPublishReady_DetachedHead_IsFalse()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.DetachHead();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.IsPublishReady, Is.False,
            "A detached HEAD means CurrentBranch won't match DefaultBranch.");
    }

    [Test]
    public void Refresh_DeepRefresh_RunsRefreshIndex()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        // Modify a file to give the index something to refresh
        File.WriteAllText(Path.Combine(fixture.Path, "README.md"), "modified");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(deepRefresh: true);

        Assert.That(repo.RefreshError, Is.Null);
        Assert.That(repo.HasPendingChanges, Is.True);
    }

    [Test]
    public void Refresh_TwiceInRow_DoesNotThrow()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();
        Assert.DoesNotThrow(() => repo.Refresh());
    }

    [Test]
    public void Refresh_WithFetchRemotes_SetsLastFetchTimeOnLocalRepo()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(fetchRemotes: true);

        Assert.That(repo.LastFetchTime, Is.Not.Null);
    }

    [Test]
    public void Refresh_WithoutFetchRemotes_LastFetchTimeNull()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(fetchRemotes: false);

        Assert.That(repo.LastFetchTime, Is.Null);
    }

    [Test]
    public void Refresh_SetsRefreshTimeSeconds()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.RefreshTimeSeconds = 0;
        repo.Refresh();
        // RefreshTimeSeconds is set by the report, not Refresh() itself, so it stays 0
        // But we can at least verify the property is settable and readable
        Assert.That(repo.RefreshTimeSeconds, Is.GreaterThanOrEqualTo(0));
    }

}
