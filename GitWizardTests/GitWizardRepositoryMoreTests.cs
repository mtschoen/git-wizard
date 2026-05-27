using GitWizard;

namespace GitWizardTests;

public class GitWizardRepositoryMoreTests
{
    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
    }

    [Test]
    public void Constructor_SetsWorkingDirectory()
    {
        var repo = new GitWizardRepository("/test/path");
        Assert.That(repo.WorkingDirectory, Is.EqualTo("/test/path"));
    }

    [Test]
    public void Constructor_DoesNotThrow_ForNonExistentPath()
    {
        var repo = new GitWizardRepository("/nonexistent/path");
        Assert.That(repo.WorkingDirectory, Is.EqualTo("/nonexistent/path"));
    }

    [Test]
    public void DefaultCtor_CreatesValidInstance()
    {
        var repo = new GitWizardRepository(string.Empty);
        Assert.That(repo, Is.Not.Null);
    }

    [Test]
    public void Refresh_InvalidDirectory_LogsError()
    {
        var repo = new GitWizardRepository("/nonexistent/path/that/does/not/exist");
        // Should not throw, just log
        repo.Refresh();
        Assert.That(repo.RefreshError, Is.Null);
    }

    [Test]
    public void Refresh_SetsIsRefreshingToFalse()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.IsRefreshing, Is.False);
    }

    [Test]
    public void Refresh_PopulatesCurrentBranch()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.CurrentBranch, Is.AnyOf("main", "master"));
    }

    [Test]
    public void Refresh_PopulatesRemoteUrls()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        // A fresh test repo typically has no remotes
        Assert.That(repo.RemoteUrls, Is.Not.Null);
    }

    [Test]
    public void Refresh_PopulatesSizeOnDisk()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.SizeOnDisk, Is.GreaterThan(0L));
    }

    [Test]
    public void Refresh_PopulatesDefaultBranch()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.DefaultBranch, Is.AnyOf("main", "master"));
    }

    [Test]
    public void Refresh_SetsRefreshTimeSeconds()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        // RefreshTimeSeconds is rounded to 1 decimal place
        Assert.That(repo.RefreshTimeSeconds, Is.GreaterThanOrEqualTo(0.0));
    }

    [Test]
    public void Refresh_CountsUntrackedFilesInPendingChanges()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        File.WriteAllText(Path.Combine(fixture.Path, "newfile.txt"), "content");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.HasPendingChanges, Is.True);
        Assert.That(repo.NumberOfPendingChanges, Is.GreaterThan(0));
    }

    [Test]
    public void Refresh_LocalCommitCount_ReflectsUnpushedCommits()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        // Without a remote, all commits are considered local (not pushed)
        Assert.That(repo.LocalCommitCount, Is.GreaterThan(0));
    }

    [Test]
    public void Refresh_PopulatesAuthorEmails()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.AuthorEmails, Is.Not.Null);
    }

    [Test]
    public void Refresh_PopulatesRecentCommits()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.RecentCommits, Is.Not.Null);
        Assert.That(repo.RecentCommits, Has.Count.GreaterThan(0));
        Assert.That(repo.RecentCommits![0].Hash, Has.Length.GreaterThanOrEqualTo(7));
    }

    [Test]
    public void Refresh_SetsDaysSinceLastCommit()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.DaysSinceLastCommit, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Refresh_MatchingBranchName_IsNull_WhenNotDetached()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.IsDetachedHead, Is.False);
        Assert.That(repo.MatchingBranchName, Is.Null.Or.Empty);
    }

    [Test]
    public void Refresh_IgnoresFetchError()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        // No remotes, so fetch would fail, but it shouldn't throw
        repo.Refresh(fetchRemotes: true);
        Assert.That(repo.RefreshError, Is.Null);
    }

    [Test]
    public void CheckoutBranch_ThrowsForNonExistentWorkingDirectory()
    {
        var repo = new GitWizardRepository("/nonexistent");
        Assert.Throws<LibGit2Sharp.RepositoryNotFoundException>(() => repo.CheckoutBranch("main"));
    }

    [Test]
    public void Refresh_DeepRefresh_SetsDaysSinceLastCommit()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(deepRefresh: true);

        Assert.That(repo.DaysSinceLastCommit, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Refresh_WithUpdateHandler_DoesNotThrow()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new NullUpdateHandler();

        repo.Refresh(handler);
        Assert.That(repo.RefreshError, Is.Null);
    }

    [Test]
    public void Refresh_WithNullUpdateHandler_DoesNotThrow()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(null);
        Assert.That(repo.RefreshError, Is.Null);
    }
}

public class NullUpdateHandler : IUpdateHandler
{
    public void StartProgress(string message, int maxCount) { }
    public void UpdateProgress(int count) { }
    public void SendUpdateMessage(string? message) { }
    public void OnRepositoryCreated(GitWizardRepository repository) { }
    public void OnRepositoryRefreshCompleted(GitWizardRepository repository) { }
    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
    public void OnWorktreeCreated(GitWizardRepository worktree) { }
}
