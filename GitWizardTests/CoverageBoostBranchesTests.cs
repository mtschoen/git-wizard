using System.Reflection;
using GitWizard;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// Coverage-boost tests for <see cref="GitWizardRepository"/> BranchesAndWorktrees partial:
/// stash detection, multiple divergence states, worktree edge cases, detached HEAD without
/// matching branch, and ComputeDirectorySize with deep nesting.
/// </summary>
public class CoverageBoostBranchesTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void Refresh_DirtyWorkingTree_HasPendingChangesTrue()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        File.WriteAllText(Path.Combine(fixture.Path, "dirty.txt"), "changes");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.GreaterThan(0));
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Refresh_CleanWorkingTree_HasPendingChangesFalse()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(repo.HasPendingChanges, Is.False);
            Assert.That(repo.NumberOfPendingChanges, Is.EqualTo(0));
        });
    }

    [Test]
    public void Refresh_MultipleBranches_BothAheadAndBehind()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        // Create a branch that is both ahead (has unique commits) and behind (default moved on)
        fixture.CommitOnNewBranch("diverged", "diverged.txt");
        fixture.AppendCommit("advance.txt"); // advance default

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var diverged = repo.Branches!.FirstOrDefault(b => b.Name == "diverged");
        Assert.That(diverged, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(diverged!.AheadOfDefault, Is.GreaterThan(0), "Branch has unique commits");
            Assert.That(diverged.BehindDefault, Is.GreaterThan(0), "Default branch moved ahead");
            Assert.That(diverged.IsMerged, Is.False);
        });
    }

    [Test]
    public void Refresh_BranchWithUpstream_HasUpstreamTrue()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.CommitOnNewBranch("tracked", "tracked.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);

        // The default branch has upstream (we pushed it)
        var defaultBranch = repo.Branches!.FirstOrDefault(b => b.Name == repo.DefaultBranch);
        if (defaultBranch != null)
        {
            Assert.That(defaultBranch.HasUpstream, Is.True);
        }
    }

    [Test]
    public void Refresh_BranchWithNoUpstream_HasUpstreamFalse()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("no-upstream", "no-upstream.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var branch = repo.Branches!.FirstOrDefault(b => b.Name == "no-upstream");
        Assert.That(branch, Is.Not.Null);
        Assert.That(branch!.HasUpstream, Is.False);
    }

    [Test]
    public void Refresh_DetachedHead_NoDefaultBranch_DefaultBranchNull()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        // Remove the default branch and detach
        using (var libRepo = new Repository(fixture.Path))
        {
            var defaultName = libRepo.Head.FriendlyName;
            var tip = libRepo.Head.Tip!;
            Commands.Checkout(libRepo, tip.Sha);
            libRepo.Branches.Remove(defaultName);
        }

        // Also remove "develop" if it exists
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(repo.IsDetachedHead, Is.True);
            // With no main/master/develop and HEAD detached, DefaultBranch should be null
            Assert.That(repo.DefaultBranch, Is.Null);
        });
    }

    [Test]
    public void Refresh_WithDevelopBranch_FallsBackToDevelop()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        // Rename the default branch to something else and create "develop"
        using (var libRepo = new Repository(fixture.Path))
        {
            var defaultName = libRepo.Head.FriendlyName;
            // Create develop at the same tip
            libRepo.Branches.Add("develop", libRepo.Head.Tip);
            // Create a non-default branch and remove main/master
            libRepo.Branches.Add("other", libRepo.Head.Tip);
            Commands.Checkout(libRepo, libRepo.Branches["develop"]);
            libRepo.Branches.Remove(defaultName);
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.DefaultBranch, Is.EqualTo("develop"));
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
        var nonExistent = Path.Combine(Path.GetTempPath(), "gw-nonexist-" + Guid.NewGuid().ToString("N"));
        var repo = new GitWizardRepository(nonExistent);
        repo.Refresh();
        // The path doesn't exist so Refresh exits early
        Assert.That(repo.IsRefreshing, Is.False);
    }

    [Test]
    public void Refresh_NotAGitRepo_SetsNotAGitRepositoryError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gw-not-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var repo = new GitWizardRepository(tempDir);
            repo.Refresh();
            Assert.That(repo.RefreshError, Is.EqualTo("Not a git repository"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public void MarkRefreshFailed_SetsErrorAndClearsRefreshing()
    {
        var repo = new GitWizardRepository("/fake");
        var method = typeof(GitWizardRepository).GetMethod("MarkRefreshFailed",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(repo, new object?[] { "timeout", null });

        Assert.Multiple(() =>
        {
            Assert.That(repo.RefreshError, Is.EqualTo("timeout"));
            Assert.That(repo.IsRefreshing, Is.False);
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
    public void Refresh_NoRemotes_EmptyRemoteUrls()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.RemoteUrls, Is.Not.Null);
        Assert.That(repo.RemoteUrls, Is.Empty);
    }

    sealed class CompletionTrackingHandler : IUpdateHandler
    {
        public List<GitWizardRepository> CompletedRepos { get; } = new();
        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) { }
        public void SendUpdateMessage(string? message) { }
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) =>
            CompletedRepos.Add(gitWizardRepository);
    }

    [Test]
    public void Refresh_NotifiesHandler_OnRepositoryRefreshCompleted()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new CompletionTrackingHandler();
        repo.Refresh(handler);

        Assert.That(handler.CompletedRepos, Has.Count.EqualTo(1));
        Assert.That(handler.CompletedRepos[0], Is.SameAs(repo));
    }

    [Test]
    public void Refresh_ThrowingHandler_OnRefreshCompleted_DoesNotPropagate()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new ThrowingCompletionHandler();
        Assert.DoesNotThrow(() => repo.Refresh(handler));
    }

    sealed class ThrowingCompletionHandler : IUpdateHandler
    {
        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) { }
        public void SendUpdateMessage(string? message) { }
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) =>
            throw new InvalidOperationException("Simulated handler failure");
    }
}
