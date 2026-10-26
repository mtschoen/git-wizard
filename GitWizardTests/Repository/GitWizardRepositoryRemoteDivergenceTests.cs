using GitWizard;

namespace GitWizardTests;

/// <summary>
/// Covers <see cref="GitWizardRepository.BehindRemoteCount"/>, <see cref="GitWizardRepository.AheadOfRemoteCount"/>,
/// and <see cref="GitWizardRepository.LastFetchTime"/> - the behind-remote detection added for
/// git-wizard#78 (the checked-out branch's divergence from its own upstream, independent of the
/// "actionable branches vs local default" view covered by <see cref="GitWizardRepositoryBranchesTests"/>).
/// </summary>
public class GitWizardRepositoryRemoteDivergenceTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void Refresh_NoUpstream_LeavesRemoteDivergenceAtZero()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.BehindRemoteCount, Is.Zero);
            Assert.That(repo.AheadOfRemoteCount, Is.Zero);
            Assert.That(repo.LastFetchTime, Is.Null,
                "fetchRemotes defaults to false, so no fetch should be recorded.");
        }
    }

    [Test]
    public void Refresh_UpToDateWithUpstream_LeavesRemoteDivergenceAtZero()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.BehindRemoteCount, Is.Zero);
            Assert.That(repo.AheadOfRemoteCount, Is.Zero);
        }
    }

    [Test]
    public void Refresh_LocalCommitNotPushed_CountsAheadOfRemote()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.AppendCommit("local-only.txt");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.AheadOfRemoteCount, Is.EqualTo(1));
            Assert.That(repo.BehindRemoteCount, Is.Zero);
        }
    }

    [Test]
    public void Refresh_WithFetch_DetectsBehindRemote_AndRecordsLastFetchTime()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.AdvanceOriginIndependently("upstream-change.txt");
        var repo = new GitWizardRepository(fixture.Path);
        var before = DateTimeOffset.Now;

        repo.Refresh(fetchRemotes: true);

        var after = DateTimeOffset.Now;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.BehindRemoteCount, Is.EqualTo(1));
            Assert.That(repo.AheadOfRemoteCount, Is.Zero);
            Assert.That(repo.LastFetchTime, Is.Not.Null);
            Assert.That(repo.LastFetchTime!.Value, Is.InRange(before, after),
                "LastFetchTime must reflect when this refresh's fetch ran.");
        }
    }

    [Test]
    public void Refresh_WithoutFetch_DoesNotSeeRemoteAdvance_AndLeavesLastFetchTimeNull()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.AdvanceOriginIndependently("upstream-change.txt");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.BehindRemoteCount, Is.Zero);
            Assert.That(repo.LastFetchTime, Is.Null);
        }
    }

    [Test]
    public void IsPublishReady_CleanUpToDateOnDefaultBranch_IsTrue()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.That(repo.IsPublishReady, Is.True);
    }

    [Test]
    public void IsPublishReady_CleanOnDefaultBranch_NoBehind_IsTrue()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.IsPublishReady, Is.True);
    }

    [Test]
    public void IsPublishReady_BehindRemote_IsFalse()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.AdvanceOriginIndependently("upstream-change.txt");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(fetchRemotes: true);

        Assert.That(repo.IsPublishReady, Is.False,
            "A checkout behind its remote must not be reported as publish-ready.");
    }

    [Test]
    public void IsPublishReady_DirtyWorkingTree_IsFalse()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.AddUntrackedFile("scratch.txt");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.That(repo.IsPublishReady, Is.False,
            "A dirty working tree must not be reported as publish-ready.");
    }

    [Test]
    public void IsPublishReady_WithPendingChanges_IsFalse()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        File.WriteAllText(Path.Combine(fixture.Path, "dirty.txt"), "dirty");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.IsPublishReady, Is.False);
    }

    [Test]
    public void IsPublishReady_NotOnDefaultBranch_IsFalse()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.CommitOnNewBranch("feature", "feature.txt");
        var repo = new GitWizardRepository(fixture.Path);
        repo.CheckoutBranch("feature");

        repo.Refresh();

        Assert.That(repo.IsPublishReady, Is.False,
            "A checkout on a non-default branch must not be reported as publish-ready.");
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
    public void Refresh_DetachedHead_LeavesRemoteDivergenceAtZero()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.DetachHead();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.IsDetachedHead, Is.True);
            Assert.That(repo.BehindRemoteCount, Is.Zero);
            Assert.That(repo.AheadOfRemoteCount, Is.Zero);
        }
    }
}
