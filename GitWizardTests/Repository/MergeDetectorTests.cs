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

    static string? DetectCheap(GitScenarioRepo scenario, string branch, string baseBranch = "main")
    {
        using var repository = new Repository(scenario.Path);
        return MergeDetector.DetectCheap(repository, repository.Branches[branch].Tip, repository.Branches[baseBranch].Tip);
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

    [TestCase("rebased", MergedBy.PatchId)]
    [TestCase("pr439-merge-only-unmerged-resolution", null)]
    [TestCase("pr439-merge-plus-cherry-picked-commit", null)]
    public void RebaseMergedBranch_IsPatchId(string mode, string? expected)
    {
        using var scenario = GitScenarioRepo.Create();
        if (mode == "rebased")
        {
            scenario.NewBranch("feature");
            scenario.Commit("a.txt", "a", "a");
            scenario.Commit("b.txt", "b", "b");
            scenario.Checkout("main");
            scenario.Commit("unrelated.txt", "unrelated", "unrelated");
            scenario.Git("cherry-pick", "main..feature");
        }
        else
        {
            scenario.Commit("main-side.txt", "main side", "advance main");
            scenario.Git("checkout", "-q", "-b", "feature", "HEAD~1");
            scenario.Git("merge", "-q", "--no-ff", "--no-commit", "main");
            scenario.Write("resolution.txt", "unmerged work in merge commit");
            scenario.Git("add", "resolution.txt");
            scenario.Git("commit", "-q", "-m", "merge main with extra work");
            if (mode == "pr439-merge-plus-cherry-picked-commit")
            {
                scenario.Commit("picked.txt", "picked", "picked commit");
                var pickedSha = scenario.Sha("feature");
                scenario.Checkout("main");
                scenario.Git("cherry-pick", pickedSha);
            }
            else
            {
                scenario.Checkout("main");
            }
        }

        Assert.That(Detect(scenario, "feature"), Is.EqualTo(expected));
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

    [TestCase("new-file")]
    [TestCase("pr439-executable-bit-only")]
    [TestCase("pr439-symlink-mode-only")]
    public void UnmergedBranch_IsNull(string mode)
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        switch (mode)
        {
            case "new-file":
                scenario.Commit("a.txt", "a", "a");
                break;
            case "pr439-executable-bit-only":
                scenario.Git("update-index", "--chmod=+x", "base.txt");
                scenario.Git("commit", "-q", "-m", "make base.txt executable");
                break;
            case "pr439-symlink-mode-only":
                var blobSha = scenario.Sha("HEAD:base.txt");
                scenario.Git("update-index", "--cacheinfo", $"120000,{blobSha},base.txt");
                scenario.Git("commit", "-q", "-m", "make base.txt symlink");
                break;
        }

        scenario.Git("checkout", "-q", "-f", "main");
        scenario.Commit("unrelated.txt", "unrelated", "unrelated");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Detect(scenario, "feature"), Is.Null);
            Assert.That(DetectCheap(scenario, "feature"), Is.Null);
        }
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
