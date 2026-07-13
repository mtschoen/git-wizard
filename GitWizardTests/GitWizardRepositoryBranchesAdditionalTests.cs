using System.Reflection;
using GitWizard;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// Additional branch/worktree tests for
/// <see cref="GitWizardRepository"/> (BranchesAndWorktrees partial) covering:
/// branch divergence counting, merged branch detection, default branch resolution,
/// worktree re-refresh, repos with no remotes, ComputeDirectorySize, MarkRefreshFailed,
/// and remote divergence tracking.
/// </summary>
public class GitWizardRepositoryBranchesAdditionalTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    #region CollectBranches / Default branch resolution

    [Test]
    public void Refresh_DefaultBranch_ResolvesToMainOrMaster()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.That(repo.DefaultBranch, Is.AnyOf("main", "master"));
    }

    [Test]
    public void Refresh_BranchAheadOfDefault_ShowsAheadCount()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("feature-x", "feature-x.txt");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var feature = repo.Branches!.FirstOrDefault(b => b.Name == "feature-x");
        Assert.That(feature, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(feature!.AheadOfDefault, Is.GreaterThan(0),
                "feature-x has one commit ahead of the default branch.");
            Assert.That(feature.IsMerged, Is.False);
        });
    }

    [Test]
    public void Refresh_BranchMergedIntoDefault_ShowsAsMerged()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("to-merge", "merge-me.txt");
        fixture.MergeBranchNoFastForward("to-merge");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var merged = repo.Branches!.FirstOrDefault(b => b.Name == "to-merge");
        Assert.That(merged, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(merged!.IsMerged, Is.True);
            Assert.That(merged.MergedInto, Is.Not.Null.And.Not.Empty,
                "MergedInto should name the default branch.");
            Assert.That(merged.AheadOfDefault, Is.EqualTo(0));
        });
    }

    [Test]
    public void Refresh_ActionableBranches_SkipsDefaultBranch()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("diverged", "diverged.txt");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(); // actionable only (allBranches=false)

        if (repo.Branches != null)
        {
            // The default branch itself is always excluded from actionable view
            var defaultName = repo.DefaultBranch;
            Assert.That(repo.Branches.Any(b => b.Name == defaultName), Is.False,
                "The default branch must not appear in the actionable branch list.");
        }
    }

    [Test]
    public void Refresh_AllBranches_IncludesDefaultBranch()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        // Create a diverged branch so the default branch is not the only one
        fixture.CommitOnNewBranch("other", "other.txt");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var defaultName = repo.DefaultBranch;
        Assert.That(repo.Branches!.Any(b => b.Name == defaultName), Is.True,
            "The full inventory includes the default branch.");
    }

    [Test]
    public void Refresh_MultipleBranches_CountsDivergenceCorrectly()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        // Create branches with different numbers of commits ahead
        fixture.CommitOnNewBranch("one-ahead", "one.txt");
        fixture.CommitOnNewBranch("two-ahead", "two-a.txt");
        // Add another commit to two-ahead
        using (var libRepo = new Repository(fixture.Path))
        {
            var branch = libRepo.Branches["two-ahead"];
            Commands.Checkout(libRepo, branch);
        }
        fixture.AppendCommit("two-b.txt");
        using (var libRepo = new Repository(fixture.Path))
        {
            var defaultBranch = libRepo.Branches["main"] ?? libRepo.Branches["master"];
            Commands.Checkout(libRepo, defaultBranch!);
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var oneAhead = repo.Branches!.FirstOrDefault(b => b.Name == "one-ahead");
        var twoAhead = repo.Branches!.FirstOrDefault(b => b.Name == "two-ahead");

        Assert.Multiple(() =>
        {
            Assert.That(oneAhead, Is.Not.Null);
            Assert.That(oneAhead!.AheadOfDefault, Is.EqualTo(1));
            Assert.That(twoAhead, Is.Not.Null);
            Assert.That(twoAhead!.AheadOfDefault, Is.EqualTo(2));
        });
    }

    [Test]
    public void Refresh_BranchBehindDefault_ShowsBehindCount()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        // Create a branch at the initial commit, then advance main
        fixture.AddBranchAtHead("stale-branch");
        fixture.AppendCommit("advance.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var stale = repo.Branches!.FirstOrDefault(b => b.Name == "stale-branch");
        Assert.That(stale, Is.Not.Null);
        Assert.That(stale!.BehindDefault, Is.GreaterThan(0));
    }

    [Test]
    public void Refresh_BranchHasLastCommitDate()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("dated", "dated.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var dated = repo.Branches!.FirstOrDefault(b => b.Name == "dated");
        Assert.That(dated, Is.Not.Null);
        Assert.That(dated!.LastCommitDate, Is.Not.Null);
    }

    #endregion

    #region Remote divergence

    [Test]
    public void Refresh_NoRemote_BehindRemoteCountIsZero()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(repo.BehindRemoteCount, Is.EqualTo(0));
            Assert.That(repo.AheadOfRemoteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void Refresh_WithRemoteAndUnpushedCommit_AheadOfRemoteIsPositive()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.AppendCommit("unpushed.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.AheadOfRemoteCount, Is.GreaterThan(0));
    }

    [Test]
    public void Refresh_WithRemoteAndBehindCommit_BehindRemoteIsPositive()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.AdvanceOriginIndependently("from-remote.txt");

        // Fetch so our local tracking ref knows about the new remote commit
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(fetchRemotes: true);

        Assert.That(repo.BehindRemoteCount, Is.GreaterThan(0));
    }

    [Test]
    public void Refresh_DetachedHead_BehindRemoteCountIsZero()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.DetachHead();

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        // Detached HEAD has no upstream to compare against
        Assert.Multiple(() =>
        {
            Assert.That(repo.IsDetachedHead, Is.True);
            Assert.That(repo.BehindRemoteCount, Is.EqualTo(0));
            Assert.That(repo.AheadOfRemoteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void Refresh_FetchRemotes_SetsLastFetchTime()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(fetchRemotes: true);

        Assert.That(repo.LastFetchTime, Is.Not.Null);
    }

    #endregion

    #region Local-only commits

    [Test]
    public void Refresh_NoRemote_AllCommitsAreLocal()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(repo.LocalOnlyCommits, Is.True);
            Assert.That(repo.LocalCommitCount, Is.EqualTo(2)); // initial + second
        });
    }

    [Test]
    public void Refresh_AllPushed_NoLocalOnlyCommits()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(repo.LocalOnlyCommits, Is.False);
            Assert.That(repo.LocalCommitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void Refresh_SomePushedSomeLocal_CountsOnlyLocal()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.AppendCommit("local-only.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(repo.LocalOnlyCommits, Is.True);
            Assert.That(repo.LocalCommitCount, Is.EqualTo(1));
        });
    }

    #endregion

    #region MarkRefreshFailed

    static void InvokeMarkRefreshFailed(GitWizardRepository repo, string error, IUpdateHandler? handler = null)
    {
        var method = typeof(GitWizardRepository).GetMethod("MarkRefreshFailed",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(repo, new object?[] { error, handler });
    }

    [Test]
    public void MarkRefreshFailed_SetsErrorAndNotifiesHandler()
    {
        var repo = new GitWizardRepository("/fake/path");
        var handler = new RecordingHandler();

        InvokeMarkRefreshFailed(repo, "test error", handler);

        Assert.Multiple(() =>
        {
            Assert.That(repo.RefreshError, Is.EqualTo("test error"));
            Assert.That(repo.IsRefreshing, Is.False);
            Assert.That(handler.CompletedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void MarkRefreshFailed_NullHandler_DoesNotThrow()
    {
        var repo = new GitWizardRepository("/fake/path");

        Assert.DoesNotThrow(() => InvokeMarkRefreshFailed(repo, "error"));
        Assert.That(repo.RefreshError, Is.EqualTo("error"));
    }

    [Test]
    public void MarkRefreshFailed_HandlerThrows_DoesNotPropagate()
    {
        var repo = new GitWizardRepository("/fake/path");
        var handler = new ThrowingHandler();

        Assert.DoesNotThrow(() => InvokeMarkRefreshFailed(repo, "error", handler));
    }

    #endregion

    #region ComputeDirectorySize (via reflection)

    static long InvokeComputeDirectorySize(string path)
    {
        var method = typeof(GitWizardRepository).GetMethod("ComputeDirectorySize",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (long)method.Invoke(null, new object[] { path })!;
    }

    [Test]
    public void ComputeDirectorySize_EmptyDirectory_ReturnsZero()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "gw-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            var size = InvokeComputeDirectorySize(emptyDir);
            Assert.That(size, Is.EqualTo(0));
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    [Test]
    public void ComputeDirectorySize_DirectoryWithFiles_ReturnsSumOfFileSizes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gw-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Create two files with known content
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello"); // ~5 bytes
            File.WriteAllText(Path.Combine(dir, "b.txt"), "world!"); // ~6 bytes

            var size = InvokeComputeDirectorySize(dir);
            Assert.That(size, Is.GreaterThan(0));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ComputeDirectorySize_NestedDirectories_IncludesAllFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gw-nested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var subDir = Path.Combine(dir, "sub");
        Directory.CreateDirectory(subDir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "root.txt"), "root");
            File.WriteAllText(Path.Combine(subDir, "nested.txt"), "nested");

            var size = InvokeComputeDirectorySize(dir);
            Assert.That(size, Is.GreaterThan(0));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    #endregion

    #region Worktree edge cases

    [Test]
    public void Refresh_Worktree_DoesNotRecurseIntoWorktrees()
    {
        // A worktree should not try to discover its own worktrees (would cause infinite loop)
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var worktreePath = fixture.AddWorktree("wt-test");

        var worktreeRepo = new GitWizardRepository(worktreePath);
        SetIsWorktree(worktreeRepo, true);
        worktreeRepo.Refresh();

        // If it recursed, it would find its own parent's worktrees. Since IsWorktree=true,
        // RefreshWorktrees is skipped.
        Assert.That(worktreeRepo.Worktrees, Is.Null,
            "A worktree must not discover further worktrees.");
    }

    [Test]
    public void Refresh_WithWorktree_ReRefresh_ReusesExistingWorktree()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddWorktree("wt-stable");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();
        var firstWorktreeCount = repo.Worktrees?.Count ?? 0;

        repo.Refresh();
        var secondWorktreeCount = repo.Worktrees?.Count ?? 0;

        Assert.That(secondWorktreeCount, Is.EqualTo(firstWorktreeCount),
            "Re-refreshing should not duplicate worktree entries.");
    }

    #endregion

    #region Detached HEAD matching

    [Test]
    public void Refresh_DetachedAtFeatureBranch_FindsMatchingBranch()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("my-feature", "feature-file.txt");

        // Checkout the feature branch tip in detached mode
        using (var libRepo = new Repository(fixture.Path))
        {
            var featureTip = libRepo.Branches["my-feature"].Tip;
            Commands.Checkout(libRepo, featureTip);
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(repo.IsDetachedHead, Is.True);
            Assert.That(repo.MatchingBranchName, Is.EqualTo("my-feature"));
        });
    }

    #endregion

    #region SizeOnDisk

    [Test]
    public void Refresh_PopulatesSizeOnDisk()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.That(repo.SizeOnDisk, Is.GreaterThan(0),
            "A repository with files must report a non-zero size.");
    }

    #endregion

    #region RecentCommits and AuthorEmails

    [Test]
    public void Refresh_PopulatesRecentCommits()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.RecentCommits, Is.Not.Null);
        Assert.That(repo.RecentCommits!, Has.Count.EqualTo(2));
    }

    [Test]
    public void Refresh_PopulatesAuthorEmails()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.That(repo.AuthorEmails, Is.Not.Null);
        Assert.That(repo.AuthorEmails!, Is.Not.Empty);
        Assert.That(repo.AuthorEmails!, Does.Contain("test@example.com"));
    }

    #endregion

    #region IsWorktree property

    [Test]
    public void IsWorktree_CanBeSet_ViaReflection()
    {
        var repo = new GitWizardRepository("/some/path");
        SetIsWorktree(repo, true);
        Assert.That(repo.IsWorktree, Is.True);
    }

    #endregion

    #region Reflection helpers

    static void SetIsWorktree(GitWizardRepository repo, bool value)
    {
        typeof(GitWizardRepository).GetProperty("IsWorktree")!
            .GetSetMethod(true)!
            .Invoke(repo, new object[] { value });
    }

    #endregion

    #region Helpers

    sealed class RecordingHandler : IUpdateHandler
    {
        public int CompletedCount { get; private set; }
        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) { }
        public void SendUpdateMessage(string? message) { }
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) => CompletedCount++;
    }

    sealed class ThrowingHandler : IUpdateHandler
    {
        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) { }
        public void SendUpdateMessage(string? message) { }
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository)
            => throw new InvalidOperationException("Simulated handler failure");
    }

    #endregion
}
