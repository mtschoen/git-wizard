using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// Each <see cref="MergeDetector"/> tier against real repositories built with the git CLI.
/// </summary>
[TestFixture]
public class MergeDetectorTests
{
    static string? Detect(GitScenarioRepo scenario, string branch, string baseBranch = "main")
    {
        using var repository = new Repository(scenario.Path);
        return MergeDetector.Detect(repository, repository.Branches[branch].Tip, repository.Branches[baseBranch].Tip);
    }

    [Test]
    public void FastForwardMergedBranch_IsAncestor()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("feature.txt", "feature", "feature");
        scenario.Checkout("main");
        scenario.Git("merge", "-q", "--ff-only", "feature");

        Assert.That(Detect(scenario, "feature"), Is.EqualTo(MergedBy.Ancestor));
    }

    [Test]
    public void RebaseMergedBranch_IsPatchId()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("a.txt", "a", "a");
        scenario.Commit("b.txt", "b", "b");
        scenario.Checkout("main");
        scenario.Commit("unrelated.txt", "unrelated", "unrelated");
        scenario.Git("cherry-pick", "main..feature");

        Assert.That(Detect(scenario, "feature"), Is.EqualTo(MergedBy.PatchId));
    }

    [Test]
    public void SquashMergedBranch_WithLaterBaseCommits_IsSquash()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("a.txt", "a", "a");
        scenario.Commit("b.txt", "b", "b");
        scenario.Checkout("main");
        scenario.SquashMerge("feature");
        scenario.Commit("later.txt", "later", "later unrelated");
        scenario.Commit("base.txt", "base edited later", "later edit");

        Assert.That(Detect(scenario, "feature"), Is.EqualTo(MergedBy.Squash));
    }

    [Test]
    public void SquashCommitEditedDuringMerge_IsContent()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("a.txt", "a", "a");
        scenario.Commit("b.txt", "b", "b");
        scenario.Checkout("main");
        scenario.Git("merge", "-q", "--squash", "feature");
        scenario.Write("extra.txt", "reviewer fixup folded into the merge");
        scenario.Git("add", "extra.txt");
        scenario.Git("commit", "-q", "-m", "squash feature with fixup");

        Assert.That(Detect(scenario, "feature"), Is.EqualTo(MergedBy.Content));
    }

    [Test]
    public void UnmergedBranch_IsNull()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("a.txt", "a", "a");
        scenario.Checkout("main");
        scenario.Commit("unrelated.txt", "unrelated", "unrelated");

        Assert.That(Detect(scenario, "feature"), Is.Null);
    }

    [Test]
    public void BranchWhosePathWasLaterChangedOnBase_IsNull()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("a.txt", "a", "a");
        scenario.Checkout("main");
        scenario.Commit("a.txt", "a, but different", "conflicting a");

        Assert.That(Detect(scenario, "feature"), Is.Null);
    }

    [Test]
    public void UnrelatedHistory_IsNull()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.Git("checkout", "-q", "--orphan", "orphan");
        scenario.Commit("orphan.txt", "orphan", "orphan root");
        scenario.Checkout("main");

        Assert.That(Detect(scenario, "orphan"), Is.Null);
    }
}
