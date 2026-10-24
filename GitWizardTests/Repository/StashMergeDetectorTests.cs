using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

[TestFixture]
public class StashMergeDetectorTests
{
    static List<StashInventoryInfo> Inventory(GitScenarioRepo scenario)
    {
        using var repository = new Repository(scenario.Path);
        return StashMergeDetector.Inventory(repository, repository.Branches["main"].Tip);
    }

    [Test]
    public void StashFullyInBase_IsMergedByContent()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.Write("base.txt", "stashed edit");
        scenario.Git("stash", "push", "-q", "-m", "edit base");
        scenario.Commit("base.txt", "stashed edit", "land the edit");

        var stashes = Inventory(scenario);

        Assert.That(stashes, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(stashes[0].MergedBy, Is.EqualTo(MergedBy.Content));
            Assert.That(stashes[0].IsMerged, Is.True);
            Assert.That(stashes[0].Index, Is.Zero);
            Assert.That(stashes[0].Message, Does.Contain("edit base"));
            Assert.That(stashes[0].Sha, Is.EqualTo(scenario.Sha("stash@{0}")));
        });
    }

    [Test]
    public void StashPartiallyInBase_IsNotMerged()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.Commit("other.txt", "other", "add other");
        scenario.Write("base.txt", "stashed edit");
        scenario.Write("other.txt", "stashed other edit");
        scenario.Git("stash", "push", "-q", "-m", "two edits");
        scenario.Commit("base.txt", "stashed edit", "land only one edit");

        var stashes = Inventory(scenario);

        Assert.That(stashes.Single().MergedBy, Is.Null);
    }

    [Test]
    public void StashedIndexDifferingFromWorktree_IsNotMergedWhenOnlyWorktreeLanded()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.Write("base.txt", "staged version");
        scenario.Git("add", "base.txt");
        scenario.Write("base.txt", "worktree version");
        scenario.Git("stash", "push", "-q");
        scenario.Commit("base.txt", "worktree version", "land worktree version");

        Assert.That(Inventory(scenario).Single().MergedBy, Is.Null);
    }

    [Test]
    public void StashWithUntrackedFiles_IsMergedOnlyWhenUntrackedFilesAreInBase()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.Write("nested/new.txt", "untracked");
        scenario.Git("stash", "push", "-q", "-u", "-m", "untracked");

        Assert.That(Inventory(scenario).Single().MergedBy, Is.Null);

        scenario.Commit("nested/new.txt", "untracked", "land the untracked file");

        Assert.That(Inventory(scenario).Single().MergedBy, Is.EqualTo(MergedBy.Content));
    }

    [Test]
    public void NoBase_ReportsEveryStashUnmerged()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.Write("base.txt", "edit");
        scenario.Git("stash", "push", "-q");

        using var repository = new Repository(scenario.Path);
        Assert.That(StashMergeDetector.Inventory(repository, null).Single().IsMerged, Is.False);
    }
}
