using System.Text.Json;
using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// Refresh-time merge detection (cheap tiers only, against the remote-first base), stash counts
/// and the local-only branch count on <see cref="GitWizardRepository"/>.
/// </summary>
[TestFixture]
public class GitWizardRepositoryMergedByTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static GitWizardRepository Refreshed(GitScenarioRepo scenario, bool computeLocalCommitCount = true)
    {
        var repository = new GitWizardRepository(scenario.Path);
        repository.Refresh(allBranches: true, computeLocalCommitCount: computeLocalCommitCount);
        return repository;
    }

    static BranchInfo Branch(GitWizardRepository repository, string name) =>
        repository.Branches!.Single(branch => branch.Name == name);

    [Test]
    public void Refresh_AncestorBranch_SetsMergedByAncestor()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("feature.txt", "feature", "feature");
        scenario.Checkout("main");
        scenario.Git("merge", "-q", "--no-ff", "-m", "merge feature", "feature");

        var feature = Branch(Refreshed(scenario), "feature");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(feature.MergedBy, Is.EqualTo(MergedBy.Ancestor));
            Assert.That(feature.IsMerged, Is.True);
            Assert.That(feature.MergedInto, Is.EqualTo("main"));
        }
    }

    [Test]
    public void Refresh_ContentMergedBranch_SetsMergedByContent()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("a.txt", "a", "a");
        scenario.Checkout("main");
        scenario.Git("merge", "-q", "--squash", "feature");
        scenario.Write("extra.txt", "reviewer fixup folded into the merge");
        scenario.Git("add", "extra.txt");
        scenario.Git("commit", "-q", "-m", "squash feature with fixup");

        var feature = Branch(Refreshed(scenario), "feature");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(feature.MergedBy, Is.EqualTo(MergedBy.Content));
            Assert.That(feature.IsMerged, Is.True);
        }
    }

    [Test]
    public void Refresh_SquashOnlyBranch_StaysUnmergedBecauseRefreshSkipsTheCherryTiers()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("a.txt", "a", "a");
        scenario.Commit("b.txt", "b", "b");
        scenario.Checkout("main");
        scenario.SquashMerge("feature");
        scenario.Commit("a.txt", "a, edited after the squash", "later edit");

        var feature = Branch(Refreshed(scenario), "feature");

        using var library = new Repository(scenario.Path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MergeDetector.Detect(library, library.Branches["feature"].Tip, library.Branches["main"].Tip),
                Is.EqualTo(MergedBy.Squash), "precondition: only the squash tier proves this merge");
            Assert.That(feature.MergedBy, Is.Null);
            Assert.That(feature.IsMerged, Is.False);
            Assert.That(feature.MergedInto, Is.Null);
        }
    }

    [Test]
    public void Refresh_StaleLocalMain_ComparesAgainstOriginMain()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();
        scenario.NewBranch("feature");
        scenario.Commit("a.txt", "a", "a");
        scenario.Checkout("main");
        scenario.Git("merge", "-q", "--ff-only", "feature");
        scenario.Git("push", "-q", "origin", "main");
        scenario.Git("reset", "-q", "--hard", "HEAD~1");

        var repository = Refreshed(scenario);
        var feature = Branch(repository, "feature");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(feature.MergedBy, Is.EqualTo(MergedBy.Ancestor));
            Assert.That(feature.MergedInto, Is.EqualTo("origin/main"));
            Assert.That(Branch(repository, "main").MergedBy, Is.Null, "The base's local counterpart is never merged work.");
        }
    }

    [Test]
    public void Refresh_CountsStashesAndMergedStashes()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.Write("base.txt", "landed edit");
        scenario.Git("stash", "push", "-q", "-m", "landed");
        scenario.Write("base.txt", "unlanded edit");
        scenario.Git("stash", "push", "-q", "-m", "unlanded");
        scenario.Commit("base.txt", "landed edit", "land the first stash");

        var repository = Refreshed(scenario);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.StashCount, Is.EqualTo(2));
            Assert.That(repository.MergedStashCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void Refresh_LinkedWorktree_LeavesSharedStashesToTheMainCheckout()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("in-worktree");
        scenario.Checkout("main");
        scenario.Write("base.txt", "wip");
        scenario.Git("stash", "push", "-q", "-m", "wip");
        scenario.AddWorktree("in-worktree");

        var repository = Refreshed(scenario);
        var worktree = repository.Worktrees!.Values.Single()!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.StashCount, Is.EqualTo(1));
            Assert.That(worktree.StashCount, Is.Zero);
        }
    }

    [TestCase("default")]
    [TestCase("pr439-detached-no-local-default")]
    public void Refresh_LocalOnlyBranchCount_CountsUnpushedUnmergedBranchesOnly(string mode)
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();
        scenario.NewBranch("old-local");
        scenario.Write("old.txt", "old");
        scenario.Git("add", "old.txt");
        scenario.Git("commit", "-q", "--date", "2020-01-01T00:00:00Z", "-m", "old local work");
        scenario.Checkout("main");
        scenario.NewBranch("new-local");
        scenario.Commit("new.txt", "new", "new local work");
        scenario.Checkout("main");
        scenario.NewBranch("pushed");
        scenario.Commit("pushed.txt", "pushed", "pushed work");
        scenario.Git("push", "-q", "-u", "origin", "pushed");
        scenario.Checkout("main");
        scenario.NewBranch("merged-local");
        scenario.Commit("merged.txt", "merged", "merged work");
        scenario.Checkout("main");
        scenario.Git("merge", "-q", "--squash", "merged-local");
        scenario.Write("extra.txt", "fixup");
        scenario.Git("add", "extra.txt");
        scenario.Git("commit", "-q", "-m", "squash merged-local with fixup");
        scenario.Git("push", "-q", "origin", "main");
        if (mode == "pr439-detached-no-local-default")
        {
            scenario.Git("checkout", "-q", "--detach", "origin/main");
            scenario.Git("branch", "-D", "main");
        }

        var repository = Refreshed(scenario);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.LocalOnlyBranchCount, Is.EqualTo(2), "old-local and new-local; pushed and merged-local are excluded");
            Assert.That(repository.OldestLocalOnlyBranchDate, Is.EqualTo(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            Assert.That(Branch(repository, "merged-local").MergedBy, Is.EqualTo(MergedBy.Content));
        }
    }

    [Test]
    public void Refresh_WithoutLocalCommitCount_LeavesLocalOnlyBranchCountZero()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();
        scenario.NewBranch("local");
        scenario.Commit("local.txt", "local", "local work");

        var repository = Refreshed(scenario, computeLocalCommitCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.LocalOnlyBranchCount, Is.Zero);
            Assert.That(repository.OldestLocalOnlyBranchDate, Is.Null);
        }
    }

    [Test]
    public void CachedReportWithoutNewFields_LoadsWithDefaults()
    {
        const string json = """{"Branches":[{"Name":"feature","IsMerged":true,"MergedInto":"main"}],"RefreshTimeSeconds":1.5}""";

        var repository = JsonSerializer.Deserialize<GitWizardRepository>(json)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.StashCount, Is.Zero);
            Assert.That(repository.MergedStashCount, Is.Zero);
            Assert.That(repository.LocalOnlyBranchCount, Is.Zero);
            Assert.That(repository.OldestLocalOnlyBranchDate, Is.Null);
            Assert.That(repository.Branches!.Single().IsMerged, Is.True);
            Assert.That(repository.Branches!.Single().MergedBy, Is.Null);
        }
    }

    [Test]
    public void NewFields_RoundTripThroughJson()
    {
        var date = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var original = new GitWizardRepository("/repo")
        {
            StashCount = 3,
            MergedStashCount = 1,
            LocalOnlyBranchCount = 2,
            OldestLocalOnlyBranchDate = date,
            Branches = [new BranchInfo { Name = "feature", IsMerged = true, MergedBy = MergedBy.Content }]
        };

        var copy = JsonSerializer.Deserialize<GitWizardRepository>(JsonSerializer.Serialize(original))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(copy.StashCount, Is.EqualTo(3));
            Assert.That(copy.MergedStashCount, Is.EqualTo(1));
            Assert.That(copy.LocalOnlyBranchCount, Is.EqualTo(2));
            Assert.That(copy.OldestLocalOnlyBranchDate, Is.EqualTo(date));
            Assert.That(copy.Branches!.Single().MergedBy, Is.EqualTo(MergedBy.Content));
        }
    }
}
