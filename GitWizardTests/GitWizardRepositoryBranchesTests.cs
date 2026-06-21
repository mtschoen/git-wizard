using GitWizard;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// Covers the branch/worktree/checkout paths in
/// <see cref="GitWizardRepository"/> (the BranchesAndWorktrees partial): detached-HEAD branch
/// matching, worktree discovery, the full-inventory branch view, the deep-refresh index pass, and
/// <see cref="GitWizardRepository.CheckoutBranch"/>.
/// </summary>
public class GitWizardRepositoryBranchesTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void CheckoutBranch_MovesWorkingTreeToBranchTip()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("feature", "feature.txt");
        var repo = new GitWizardRepository(fixture.Path);

        repo.CheckoutBranch("feature");

        using var library = new Repository(fixture.Path);
        var featureTip = library.Branches["feature"].Tip!.Sha;
        Assert.Multiple(() =>
        {
            Assert.That(library.Head.Tip!.Sha, Is.EqualTo(featureTip),
                "Checkout must move HEAD to the named branch's tip commit.");
            Assert.That(File.Exists(Path.Combine(fixture.Path, "feature.txt")), Is.True,
                "The branch's file must be present in the working tree after checkout.");
        });
    }

    [Test]
    public void CheckoutBranch_ThrowsWhenWorkingDirectoryEmpty()
    {
        var repo = new GitWizardRepository(string.Empty);

        Assert.Throws<InvalidOperationException>(() => repo.CheckoutBranch("main"));
    }

    [Test]
    public void Refresh_DetachedHeadAtBranchTip_FindsMatchingBranch()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.DetachHead();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(repo.IsDetachedHead, Is.True);
            Assert.That(repo.MatchingBranchName, Is.AnyOf("main", "master"),
                "A detached HEAD sitting on the default branch's tip must resolve to that branch.");
        });
    }

    [Test]
    public void Refresh_DetachedHeadOnUniqueCommit_LeavesMatchingBranchNull()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");
        // Detach one commit back: no branch tip points at the parent commit.
        fixture.DetachHead("HEAD~1");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(repo.IsDetachedHead, Is.True);
            Assert.That(repo.MatchingBranchName, Is.Null,
                "No local branch points at this commit, so no match must be recorded.");
        });
    }

    [Test]
    public void Refresh_PopulatesWorktrees_AndNotifiesHandler()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var worktreePath = fixture.AddWorktree("wt-branch");
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new RecordingWorktreeHandler();

        repo.Refresh(handler);

        Assert.Multiple(() =>
        {
            Assert.That(repo.Worktrees, Is.Not.Null);
            Assert.That(repo.Worktrees!, Is.Not.Empty, "The linked worktree must be discovered.");
            Assert.That(repo.IsWorktree, Is.False, "The primary repository is not itself a worktree.");
            Assert.That(handler.WorktreesCreated, Is.GreaterThan(0),
                "OnWorktreeCreated must fire for the newly discovered worktree.");
        });

        // The discovered worktree's working directory must point at the path we added (case- and
        // separator-insensitive: the code lowercases and the OS may differ on slashes).
        var discovered = repo.Worktrees!.Values
            .Where(w => w != null)
            .Select(w => w!.WorkingDirectory!.TrimEnd('/', '\\'))
            .ToList();
        var expected = worktreePath.Replace('\\', '/').TrimEnd('/');
        Assert.That(discovered.Any(d => string.Equals(
            d.Replace('\\', '/'), expected, StringComparison.OrdinalIgnoreCase)), Is.True,
            $"Expected a worktree at {expected}; got [{string.Join(", ", discovered)}].");
    }

    [Test]
    public void Refresh_AllBranches_KeepsBranchAtDefaultTip()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddBranchAtHead("at-tip");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        Assert.That(repo.Branches!.Any(b => b.Name == "at-tip"), Is.True,
            "The full inventory must include a branch sitting exactly at the default tip.");
    }

    [Test]
    public void Refresh_ActionableBranches_DropsBranchAtDefaultTip()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddBranchAtHead("at-tip");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(); // allBranches defaults to false (actionable view)

        // A branch identical to the default branch is "boring" and excluded from the actionable view.
        var actionable = repo.Branches ?? new List<BranchInfo>();
        Assert.That(actionable.Any(b => b.Name == "at-tip"), Is.False,
            "The actionable view must drop a branch sitting exactly at the default tip.");
    }

    [Test]
    public void Refresh_DeepRefresh_RefreshesIndexWithoutError()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(deepRefresh: true);

        Assert.That(repo.RefreshError, Is.Null,
            "Deep refresh runs the git index-refresh pass and must complete cleanly.");
    }

    sealed class RecordingWorktreeHandler : IUpdateHandler
    {
        public int WorktreesCreated { get; private set; }

        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) { }
        public void SendUpdateMessage(string? message) { }
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) => WorktreesCreated++;
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    }
}
