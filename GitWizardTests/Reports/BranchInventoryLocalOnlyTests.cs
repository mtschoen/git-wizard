using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// <see cref="BranchInventoryReport"/> tiered merge detection, remote-first base resolution,
/// stash listing and the <c>--local-only</c> filter.
/// </summary>
[TestFixture]
public class BranchInventoryLocalOnlyTests
{
    [Test]
    public void StaleLocalMain_ComparesAgainstOriginMainContainingTheSquash()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();
        scenario.NewBranch("feature");
        scenario.Commit("a.txt", "a", "a");
        scenario.Commit("b.txt", "b", "b");
        scenario.Checkout("main");
        scenario.SquashMerge("feature");
        scenario.Git("push", "-q", "origin", "main");
        scenario.Git("reset", "-q", "--hard", "HEAD~1");

        var report = BranchInventoryReport.Scan([scenario.Path]);

        var repository = report.Repositories.Single();
        var feature = repository.Branches.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.SchemaVersion, Is.EqualTo("1.1"));
            Assert.That(repository.DefaultBranch, Is.EqualTo("origin/main"));
            Assert.That(feature.Name, Is.EqualTo("feature"));
            Assert.That(feature.MergedBy, Is.EqualTo(MergedBy.Squash));
            Assert.That(feature.IsMerged, Is.True);
        }
    }

    [TestCase("exact")]
    [TestCase("pr439-windows-case-variants")]
    public void LinkedWorktreePath_IsScannedOnceWithItsMainCheckout(string mode)
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();
        scenario.NewBranch("local-work");
        scenario.Commit("local.txt", "local", "local work");
        scenario.Checkout("main");
        scenario.NewBranch("in-worktree");
        scenario.Checkout("main");
        scenario.Write("wip.txt", "wip");
        scenario.Git("stash", "push", "-u", "-q", "-m", "wip");
        var worktreePath = scenario.AddWorktree("in-worktree");
        var paths = mode == "pr439-windows-case-variants" && OperatingSystem.IsWindows()
            ? new[] { scenario.Path.ToLowerInvariant(), scenario.Path.ToUpperInvariant(), worktreePath.ToUpperInvariant() }
            : new[] { scenario.Path, worktreePath };

        var report = BranchInventoryReport.Scan(paths, localOnly: true);

        var repository = report.Repositories.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.Path, Is.EqualTo(paths[0]));
            Assert.That(repository.Branches.Select(branch => branch.Name), Is.EqualTo(["local-work"]));
            Assert.That(repository.Stashes, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void LocalOnly_KeepsLocalCounterpartOfRemoteBaseWhenItHasUnpushedCommits()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();
        scenario.Commit("unpushed.txt", "unpushed", "unpushed work on main");

        var localOnly = BranchInventoryReport.Scan([scenario.Path], localOnly: true).Repositories.Single();

        var main = localOnly.Branches.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(localOnly.DefaultBranch, Is.EqualTo("origin/main"));
            Assert.That(main.Name, Is.EqualTo("main"));
            Assert.That(main.UniqueCommitCount, Is.EqualTo(1));
            Assert.That(main.IsMerged, Is.False);
        }
    }

    [Test]
    public void LocalOnly_KeepsUnpushedUnmergedBranchAndDropsMergedOrPushedOnes()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();
        scenario.NewBranch("local-work");
        scenario.Commit("local.txt", "local", "local work");
        scenario.Checkout("main");
        scenario.NewBranch("pushed-work");
        scenario.Commit("pushed.txt", "pushed", "pushed work");
        scenario.Git("push", "-q", "-u", "origin", "pushed-work");
        scenario.Checkout("main");
        scenario.NewBranch("squashed");
        scenario.Commit("squashed.txt", "squashed", "squashed work");
        scenario.Checkout("main");
        scenario.SquashMerge("squashed");
        scenario.Git("push", "-q", "origin", "main");

        var full = BranchInventoryReport.Scan([scenario.Path]).Repositories.Single().Branches;
        var localOnly = BranchInventoryReport.Scan([scenario.Path], localOnly: true).Repositories.Single().Branches;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(full.Select(branch => branch.Name), Is.EquivalentTo(["local-work", "pushed-work", "squashed"]));
            Assert.That(full.Single(branch => branch.Name == "pushed-work").UniqueCommitCount, Is.Zero);
            Assert.That(full.Single(branch => branch.Name == "squashed").UniqueCommitCount, Is.EqualTo(1));
            Assert.That(localOnly.Select(branch => branch.Name), Is.EqualTo(["local-work"]));
            Assert.That(localOnly.Single().MergedBy, Is.Null);
        }
    }

    [Test]
    public void LocalOnly_KeepsOnlyUnmergedStashes()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.Write("base.txt", "landed edit");
        scenario.Git("stash", "push", "-q", "-m", "landed");
        scenario.Write("base.txt", "unlanded edit");
        scenario.Git("stash", "push", "-q", "-m", "unlanded");
        scenario.Commit("base.txt", "landed edit", "land the first stash");

        var full = BranchInventoryReport.Scan([scenario.Path]).Repositories.Single().Stashes;
        var localOnly = BranchInventoryReport.Scan([scenario.Path], localOnly: true).Repositories.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(full.Select(stash => stash.IsMerged), Is.EqualTo([false, true]));
            Assert.That(localOnly.Branches, Is.Empty);
            Assert.That(localOnly.Stashes.Single().Message, Does.Contain("unlanded"));
        }
    }

    [Test]
    public void LocalOnly_OmitsRepositoryWithNothingLeft()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("a.txt", "a", "a");
        scenario.Checkout("main");
        scenario.SquashMerge("feature");

        Assert.That(BranchInventoryReport.Scan([scenario.Path], localOnly: true).Repositories, Is.Empty);
    }
}
