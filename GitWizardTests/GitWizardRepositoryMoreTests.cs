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

        repo.Refresh();
        Assert.That(repo.RefreshError, Is.Null);
    }

    [Test]
    public void Refresh_NonGitDirectory_SetsNotARepositoryError()
    {
        var directory = CreateNonGitDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "file.txt"), "content");
            var repo = new GitWizardRepository(directory);

            repo.Refresh();

            Assert.That(repo.RefreshError, Does.Contain("not a git repository").IgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Refresh_InvokesRefreshCompletedCallbackExactlyOnce()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var handler = new CountingUpdateHandler();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(handler);

        Assert.That(handler.RefreshCompletedCount, Is.EqualTo(1));
    }

    [Test]
    public void Refresh_SwallowsAThrowingRefreshCompletedCallback()
    {
        var directory = CreateNonGitDirectory();
        try
        {
            var repo = new GitWizardRepository(directory);

            // The handler throws from OnRepositoryRefreshCompleted; Refresh must neither propagate it
            // nor lose the original failure reason.
            repo.Refresh(new FailingUpdateHandler());

            Assert.That(repo.RefreshError, Does.Contain("not a git repository").IgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Refresh_SetsLastCommitDate()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.That(repo.LastCommitDate, Is.Not.Null);
    }

    [Test]
    public void Refresh_AuthorEmails_ContainTheCommitterEmail()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.That(repo.AuthorEmails, Is.Not.Null);
        Assert.That(repo.AuthorEmails, Does.Contain("test@example.com"));
    }

    [Test]
    public void Refresh_RemoteUrls_AreEmpty_ForRepoWithoutRemotes()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.That(repo.RemoteUrls, Is.Empty);
    }

    [Test]
    public void Refresh_CleanRepo_HasNoPendingChanges()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.That(repo.HasPendingChanges, Is.False);
        Assert.That(repo.NumberOfPendingChanges, Is.EqualTo(0));
    }

    [Test]
    public void Refresh_ComputeLocalCommitCount_True_PopulatesLocalCommitCount()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");
        fixture.AppendCommit("third.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(computeLocalCommitCount: true);

        Assert.That(repo.LocalCommitCount, Is.EqualTo(3));
        Assert.That(repo.LocalOnlyCommits, Is.True);
    }

    [Test]
    public void Refresh_ComputeLocalCommitCount_SkipsBranchIteration()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(computeLocalCommitCount: false);

        Assert.That(repo.LocalCommitCount, Is.EqualTo(0));
        Assert.That(repo.LocalOnlyCommits, Is.False);
    }

    [Test]
    public void Refresh_ComputeLocalCommitCount_Default_ComputesCount()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(); // default computeLocalCommitCount=true

        Assert.That(repo.LocalCommitCount, Is.EqualTo(2));
        Assert.That(repo.LocalOnlyCommits, Is.True);
    }

    static string CreateNonGitDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gw-nongit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

public class NullUpdateHandler : IUpdateHandler
{
    public void StartProgress(string description, int total) { }
    public void UpdateProgress(int count) { }
    public void SendUpdateMessage(string? message) { }
    public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
    public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
    public void OnWorktreeCreated(GitWizardRepository worktree) { }
}
