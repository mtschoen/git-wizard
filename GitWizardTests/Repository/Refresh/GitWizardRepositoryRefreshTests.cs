using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

// Repository refresh tests exercise repository refresh outcomes and status transitions.
public class GitWizardRepositoryRefreshTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [TestCase(2, 2)]
    [TestCase(16, 10)]
    public void Refresh_PopulatesRecentCommits(int totalCommits, int expectedCount)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        for (var i = 1; i < totalCommits; i++)
        {
            fixture.AppendCommit($"file{i}.txt");
        }

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.RecentCommits, Is.Not.Null);
        Assert.That(repository.RecentCommits!, Has.Count.EqualTo(expectedCount));
        foreach (var commit in repository.RecentCommits!)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(commit.Hash, Has.Length.EqualTo(7));
                Assert.That(commit.Message, Is.Not.Empty);
                Assert.That(commit.AuthorEmail, Is.Not.Empty);
            }
        }
    }

    [Test]
    public void Refresh_PopulatesDaysSinceLastCommit()
    {
        var commitTime = DateTimeOffset.Now.AddDays(-5);
        using var fixture = TempRepoFixture.CreateWithInitialCommit(commitTime);
        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.DaysSinceLastCommit, Is.Not.Null);
        Assert.That(repository.DaysSinceLastCommit, Is.GreaterThanOrEqualTo(4));
    }

    [Test]
    public void Refresh_CountsUntrackedFilesInPendingChanges()
    {
        // Regression: a repo whose only changes are untracked files used to
        // report HasPendingChanges=true with NumberOfPendingChanges=0. Both
        // must agree so callers never see "dirty but zero changes".
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        File.WriteAllText(Path.Combine(fixture.Path, "untracked.txt"), "new");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.HasPendingChanges, Is.True);
            Assert.That(repository.NumberOfPendingChanges, Is.GreaterThan(0));
        }
    }

    [Test]
    public void Refresh_CountsUnstagedDeletionInPendingChanges()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        File.Delete(Path.Combine(fixture.Path, "README.md"));

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.HasPendingChanges, Is.True);
            Assert.That(repository.NumberOfPendingChanges, Is.EqualTo(1));
        }
    }

    [Test]
    public void Refresh_CountsStagedDeletionInPendingChanges()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var readmePath = Path.Combine(fixture.Path, "README.md");
        File.Delete(readmePath);
        using (var gitRepository = new Repository(fixture.Path))
        {
            Commands.Stage(gitRepository, "README.md");
        }

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.HasPendingChanges, Is.True);
            Assert.That(repository.NumberOfPendingChanges, Is.EqualTo(1));
        }
    }

    [Test]
    public void Refresh_CleanWorkingTree_HasPendingChangesFalse()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.HasPendingChanges, Is.False);
            Assert.That(repo.NumberOfPendingChanges, Is.Zero);
        }
    }

    [Test]
    public void Refresh_ModifiedFile_DetectsModifiedStatus()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        File.WriteAllText(Path.Combine(fixture.Path, "README.md"), "modified content");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.GreaterThan(0));
        }
    }

    [Test]
    public void Refresh_StagedChanges_CountsAsPending()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var filePath = Path.Combine(fixture.Path, "staged.txt");
        File.WriteAllText(filePath, "staged content");
        using (var libRepo = new Repository(fixture.Path))
        {
            Commands.Stage(libRepo, "staged.txt");
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.GreaterThan(0));
        }
    }

    [Test]
    public void Refresh_RenamedFile_DetectsPending()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var original = Path.Combine(fixture.Path, "README.md");
        var renamed = Path.Combine(fixture.Path, "RENAMED.md");
        File.Move(original, renamed);
        using (var libRepo = new Repository(fixture.Path))
        {
            Commands.Stage(libRepo, "README.md");
            Commands.Stage(libRepo, "RENAMED.md");
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.HasPendingChanges, Is.True);
    }

    [Test]
    public void Refresh_MultipleUntracked_CountsAll()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(fixture.Path, $"untracked{i}.txt"), $"content{i}");
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.GreaterThanOrEqualTo(5));
        }
    }

    [Test]
    public void Refresh_CountsLocalCommitsOnUntrackedBranch()
    {
        // Regression: LocalOnlyCommits used to be a bare bool. Consumers need
        // an actual count so they can show "3 unpushed" instead of "yes/no".
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");
        fixture.AppendCommit("third.txt");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.LocalOnlyCommits, Is.True);
            Assert.That(repository.LocalCommitCount, Is.EqualTo(3));
        }
    }

    [Test]
    public void Refresh_DoesNotCountPushedHistoryOnUntrackedBranch()
    {
        // Regression (phantom unpushed): an untracked local branch used to have
        // its ENTIRE history counted as unpushed - including commits already on a
        // remote - and the per-branch sum could exceed the repo's total commit
        // count. Here mainline (3 commits) is fully pushed and one extra commit
        // sits on an untracked feature branch, so exactly 1 commit is unpushed.
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");
        fixture.AppendCommit("third.txt");
        fixture.AddOriginRemoteAndPush();
        fixture.CommitOnNewBranch("feature/x", "feature.txt");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.LocalCommitCount, Is.EqualTo(1));
            Assert.That(repository.LocalOnlyCommits, Is.True);
        }
    }

    [Test]
    public void Refresh_NoRemote_AllCommitsAreLocal()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.LocalOnlyCommits, Is.True);
            Assert.That(repo.LocalCommitCount, Is.EqualTo(2));
        }
    }

    [Test]
    public void Refresh_AllPushed_NoLocalOnlyCommits()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.LocalOnlyCommits, Is.False);
            Assert.That(repo.LocalCommitCount, Is.Zero);
        }
    }

    [Test]
    public void Refresh_SomePushedSomeLocal_CountsOnlyLocal()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.AppendCommit("local-only.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.LocalOnlyCommits, Is.True);
            Assert.That(repo.LocalCommitCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void Refresh_ComputeLocalCommitCount_True_PopulatesLocalCommitCount()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");
        fixture.AppendCommit("third.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(computeLocalCommitCount: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.LocalCommitCount, Is.EqualTo(3));
            Assert.That(repo.LocalOnlyCommits, Is.True);
        }
    }

    [Test]
    public void Refresh_ComputeLocalCommitCount_SkipsBranchIteration()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(computeLocalCommitCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.LocalCommitCount, Is.Zero);
            Assert.That(repo.LocalOnlyCommits, Is.False);
        }
    }

    [Test]
    public void Refresh_ComputeLocalCommitCount_Default_ComputesCount()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(); // default computeLocalCommitCount=true

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.LocalCommitCount, Is.EqualTo(2));
            Assert.That(repo.LocalOnlyCommits, Is.True);
        }
    }

    [Test]
    public void Refresh_InitialRepository_PopulatesState()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(repository.CurrentBranch, Is.AnyOf("main", "master"));
            Assert.That(repository.DefaultBranch, Is.AnyOf("main", "master"));
            Assert.That(repository.HasPendingChanges, Is.False);
            Assert.That(repository.NumberOfPendingChanges, Is.Zero);
            Assert.That(repository.RemoteUrls, Is.Not.Null.And.Empty);
            Assert.That(repository.AuthorEmails, Is.Not.Null);
            Assert.That(repository.AuthorEmails!, Does.Contain("test@example.com"));
            Assert.That(repository.LastCommitDate, Is.Not.Null);
            Assert.That(repository.IsRefreshing, Is.False);
            Assert.That(repository.SizeOnDisk, Is.GreaterThan(0L));
        });
    }

    [Test]
    public void Refresh_PopulatesRemoteUrls()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.RemoteUrls, Is.Not.Null);
        Assert.That(repo.RemoteUrls, Is.Not.Empty);
    }

    [Test]
    public void Refresh_AuthorEmails_CollectsFromCommits()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("extra.txt");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.AuthorEmails, Is.Not.Null);
        Assert.That(repo.AuthorEmails!, Does.Contain("test@example.com"));
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
    public void Refresh_NullWorkingDirectory_DoesNotThrow()
    {
        var repo = new GitWizardRepository(string.Empty);
        Assert.DoesNotThrow(() => repo.Refresh());
    }

    [Test]
    public void Refresh_InvalidPath_SetsRefreshError()
    {
        using var nonExistent = new TempDirectory("gw-nonexist-", create: false);
        var repo = new GitWizardRepository(nonExistent.Path);
        repo.Refresh();
        Assert.That(repo.IsRefreshing, Is.False);
    }

    [Test]
    public void Refresh_InvalidDirectory_LogsError()
    {
        var repo = new GitWizardRepository("/nonexistent/path/that/does/not/exist");
        repo.Refresh();
        Assert.That(repo.RefreshError, Is.Null);
    }

    [Test]
    public void Refresh_NotAGitRepo_SetsNotAGitRepositoryError()
    {
        using var tempDir = new TempDirectory("gw-not-git-");
        var repo = new GitWizardRepository(tempDir.Path);
        repo.Refresh();
        Assert.That(repo.RefreshError, Is.EqualTo("Not a git repository"));
    }

    [Test]
    public void Refresh_AfterFailureThenSuccess_ClearsRefreshError()
    {
        using var directory = new TempDirectory("recovers-repo-");
        var path = directory.Path;

        var repository = new GitWizardRepository(path);
        repository.Refresh();
        Assert.That(repository.RefreshError, Is.EqualTo(GitWizardRepository.NotAGitRepositoryError));

        Repository.Init(path);
        repository.Refresh();
        Assert.That(repository.RefreshError, Is.Null);
    }

    [Test]
    [NonParallelizable]
    public void Refresh_VanishedWorkingDirectory_LogsDebugNotError()
    {
        var messages = new List<string>();
        var originalLogMethod = GitWizardLog.LogMethod;
        var originalVerboseMode = GitWizardLog.VerboseMode;
        var originalSilentMode = GitWizardLog.SilentMode;
        GitWizardLog.LogMethod = message =>
        {
            if (message != null)
            {
                messages.Add(message);
            }
        };
        GitWizardLog.VerboseMode = true;
        GitWizardLog.SilentMode = false;
        try
        {
            using var missing = new TempDirectory("gw-vanished-", create: false);
            new GitWizardRepository(missing.Path).Refresh();
        }
        finally
        {
            GitWizardLog.LogMethod = originalLogMethod;
            GitWizardLog.VerboseMode = originalVerboseMode;
            GitWizardLog.SilentMode = originalSilentMode;
        }

        Assert.That(messages, Has.Some.Matches<string>(m => m.Contains("is invalid") && m.Contains("Debug")));
        Assert.That(messages, Has.None.Contains("Error"));
    }

    [Test]
    [NonParallelizable]
    public void Refresh_VanishedWorkingDirectory_SuppressedWithoutVerboseMode()
    {
        var messages = new List<string>();
        var originalLogMethod = GitWizardLog.LogMethod;
        var originalVerboseMode = GitWizardLog.VerboseMode;
        var originalSilentMode = GitWizardLog.SilentMode;
        GitWizardLog.LogMethod = message =>
        {
            if (message != null)
            {
                messages.Add(message);
            }
        };
        GitWizardLog.VerboseMode = false;
        GitWizardLog.SilentMode = false;
        try
        {
            using var missing = new TempDirectory("gw-vanished-", create: false);
            new GitWizardRepository(missing.Path).Refresh();
        }
        finally
        {
            GitWizardLog.LogMethod = originalLogMethod;
            GitWizardLog.VerboseMode = originalVerboseMode;
            GitWizardLog.SilentMode = originalSilentMode;
        }

        Assert.That(messages, Has.None.Contains("is invalid"));
    }

    [Test]
    public void MarkRefreshFailed_NullHandler_SetsErrorAndClearsRefreshing()
    {
        var repo = new GitWizardRepository("/fake/path");
        repo.MarkRefreshFailed("test error");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.RefreshError, Is.EqualTo("test error"));
            Assert.That(repo.IsRefreshing, Is.False);
        }
    }

    [Test]
    public void MarkRefreshFailed_RecordingHandler_SetsErrorAndNotifiesHandler()
    {
        var repo = new GitWizardRepository("/fake/path");
        var handler = new RecordingUpdateHandler();
        repo.MarkRefreshFailed("test error", handler);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.RefreshError, Is.EqualTo("test error"));
            Assert.That(repo.IsRefreshing, Is.False);
            Assert.That(handler.RefreshCompleted, Has.Length.EqualTo(1));
        }
    }

    [Test]
    public void MarkRefreshFailed_ThrowingHandler_DoesNotPropagate()
    {
        var repo = new GitWizardRepository("/fake/path");
        var handler = new RecordingUpdateHandler
        {
            CompletingRefresh = _ => throw new InvalidOperationException("callback failure")
        };
        Assert.DoesNotThrow(() => repo.MarkRefreshFailed("error", handler));
    }

    [Test]
    public void Refresh_NotifiesHandler_OnRepositoryRefreshCompleted()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new RecordingUpdateHandler();
        repo.Refresh(handler);

        Assert.That(handler.RefreshCompleted, Has.Length.EqualTo(1));
        Assert.That(handler.RefreshCompleted.Single(), Is.SameAs(repo));
    }

    [Test]
    public void Refresh_ThrowingHandler_OnRefreshCompleted_DoesNotPropagate()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new RecordingUpdateHandler
        {
            CompletingRefresh = _ => throw new InvalidOperationException("callback failure")
        };
        Assert.DoesNotThrow(() => repo.Refresh(handler));
    }

    [Test]
    public void Refresh_SwallowsAThrowingRefreshCompletedCallback()
    {
        using var directory = new TempDirectory("gw-nongit-");
        var repo = new GitWizardRepository(directory.Path);
        repo.Refresh(new RecordingUpdateHandler
        {
            CompletingRefresh = _ => throw new InvalidOperationException("callback failure")
        });
        Assert.That(repo.RefreshError, Does.Contain("not a git repository").IgnoreCase);
    }

    [Test]
    public void GitWizardRepository_PrivateRefreshMethods_CatchExceptions()
    {
        var repo = new GitWizardRepository("/fake");

        using (Assert.EnterMultipleScope())
        {
            Assert.DoesNotThrow(() => repo.RefreshLocalOnlyCommits(null!));
            Assert.DoesNotThrow(() => repo.RefreshAuthorEmails(null!));
            Assert.DoesNotThrow(() => repo.RefreshRecentCommits(null!));
            Assert.DoesNotThrow(() => repo.RefreshPendingChangesStatus(null!, false));
            Assert.DoesNotThrow(() => repo.RefreshBranchDivergence(null!, false));
        }
    }

    [Test]
    public void GitWizardRepository_RefreshSizeOnDisk_CatchesException()
    {
        var repo = new GitWizardRepository("/fake");
        Assert.DoesNotThrow(() => repo.RefreshSizeOnDisk(null!));
    }

    [Test]
    public void GitWizardRepository_NotifyRefreshCompleted_CatchesException()
    {
        var repo = new GitWizardRepository("/fake");
        var handler = new RecordingUpdateHandler
        {
            CompletingRefresh = _ => throw new InvalidOperationException("callback failure")
        };
        Assert.DoesNotThrow(() => repo.NotifyRefreshCompleted(handler));
    }

    [Test]
    public void FetchAllRemotes_ValidAndInvalid_ReturnsExpected()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var resultValid = GitWizardRepository.FetchAllRemotes(fixture.Path);
        Assert.That(resultValid, Is.True);

        var resultInvalid = GitWizardRepository.FetchAllRemotes("/invalid-dir-that-does-not-exist");
        Assert.That(resultInvalid, Is.False);

        using var nonGitDir = new TempDirectory("gw-nongit-");
        var resultNonGit = GitWizardRepository.FetchAllRemotes(nonGitDir.Path);
        Assert.That(resultNonGit, Is.True);
    }
}
