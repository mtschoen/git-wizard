using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// <see cref="Pruner"/> dry-run and apply behavior, safety skips, and stash drop ordering, all
/// against throwaway repositories.
/// </summary>
[TestFixture]
public class PrunerTests
{
    static GitScenarioRepo CreateWithSquashMergedBranch(string branch)
    {
        var scenario = GitScenarioRepo.Create();
        scenario.NewBranch(branch);
        scenario.Commit($"{branch}-1.txt", branch, $"{branch} 1");
        scenario.Commit($"{branch}-2.txt", branch, $"{branch} 2");
        scenario.Checkout("main");
        scenario.SquashMerge(branch);
        return scenario;
    }

    static void StashLandedEdit(GitScenarioRepo scenario, string fileName, string content)
    {
        scenario.Write(fileName, content);
        scenario.Git("stash", "push", "-q", "-u", "-m", $"stash {fileName}");
        scenario.Commit(fileName, content, $"land {fileName}");
    }

    static PruneCandidate Find(PruneReport report, string name) => report.Candidates.Single(c => c.Name == name);

    [Test]
    public void DryRun_ReportsWouldDeleteAndDeletesNothing()
    {
        using var scenario = CreateWithSquashMergedBranch("feature");
        StashLandedEdit(scenario, "stashed.txt", "stashed");
        var stashesBefore = scenario.StashShas();
        using var hints = new StringWriter();

        var report = Pruner.Run([scenario.Path], new PruneOptions(), hints);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Applied, Is.False);
            Assert.That(report.SchemaVersion, Is.EqualTo("1.0"));
            Assert.That(Find(report, "feature").Action, Is.EqualTo(PruneCandidate.WouldDelete));
            Assert.That(Find(report, "feature").MergedBy, Is.EqualTo(MergedBy.Squash));
            Assert.That(Find(report, "stash@{0}").Action, Is.EqualTo(PruneCandidate.WouldDelete));
            Assert.That(Find(report, "stash@{0}").Kind, Is.EqualTo(PruneCandidate.StashKind));
            Assert.That(Find(report, "main").SkipReason, Is.EqualTo(PruneSkipReasons.BaseBranch));
            Assert.That(scenario.HasBranch("feature"), Is.True);
            Assert.That(scenario.StashShas(), Is.EqualTo(stashesBefore));
            Assert.That(hints.ToString(), Is.Empty);
        }
    }

    [Test]
    public void Apply_DeletesMergedBranchAndSkipsCheckedOutAndWorktreeBranches()
    {
        using var scenario = CreateWithSquashMergedBranch("merged");
        scenario.NewBranch("unmerged");
        scenario.Commit("unmerged.txt", "unmerged", "unmerged");
        scenario.Checkout("main");
        scenario.Git("branch", "in-worktree");
        scenario.Git("branch", "checked-out");
        scenario.AddWorktree("in-worktree");
        scenario.Checkout("checked-out");
        var mergedSha = scenario.Sha("merged");
        using var hints = new StringWriter();

        var report = Pruner.Run([scenario.Path], new PruneOptions(Stashes: false, Apply: true), hints);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Applied, Is.True);
            Assert.That(Find(report, "merged").Action, Is.EqualTo(PruneCandidate.Deleted));
            Assert.That(scenario.HasBranch("merged"), Is.False);
            Assert.That(hints.ToString(), Does.Contain($"branch 'merged' {mergedSha}"));
            Assert.That(Find(report, "checked-out").SkipReason, Is.EqualTo(PruneSkipReasons.CheckedOut));
            Assert.That(Find(report, "in-worktree").SkipReason, Is.EqualTo(PruneSkipReasons.CheckedOutInWorktree));
            Assert.That(Find(report, "unmerged").SkipReason, Is.EqualTo(PruneSkipReasons.NotMerged));
            Assert.That(Find(report, "unmerged").MergedBy, Is.Null);
            Assert.That(Find(report, "main").SkipReason, Is.EqualTo(PruneSkipReasons.BaseBranch));
            Assert.That(scenario.HasBranch("checked-out") && scenario.HasBranch("in-worktree") && scenario.HasBranch("unmerged"), Is.True);
            Assert.That(report.Candidates.Any(c => c.Kind == PruneCandidate.StashKind), Is.False);
        }
    }

    [Test]
    public void Apply_DropsMultipleMergedStashesByDescendingIndexAndKeepsUnmergedOne()
    {
        using var scenario = GitScenarioRepo.Create();
        StashLandedEdit(scenario, "first.txt", "first");
        scenario.Write("base.txt", "never landed");
        scenario.Git("stash", "push", "-q", "-m", "unmerged");
        StashLandedEdit(scenario, "third.txt", "third");
        var stashes = scenario.StashShas();
        using var hints = new StringWriter();

        var report = Pruner.Run([scenario.Path], new PruneOptions(Branches: false, Apply: true), hints);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Candidates.Select(c => c.Action), Is.EqualTo([
                PruneCandidate.Deleted, PruneCandidate.Skipped, PruneCandidate.Deleted
            ]));
            Assert.That(scenario.StashShas(), Is.EqualTo([stashes[1]]));
            Assert.That(hints.ToString(), Does.Contain("stash store -m 'On main: stash ").And.Contain(stashes[0]).And.Contain(stashes[2]));
            Assert.That(report.Candidates.Any(c => c.Kind == PruneCandidate.BranchKind), Is.False);
        }
    }

    [Test]
    [Platform(Exclude = "Win")]
    public void Apply_RestoreHint_IsShellSafeForHostileStashMessage()
    {
        using var scenario = GitScenarioRepo.Create();
        var marker = Path.Combine(scenario.Path, "pwned");
        const string fileName = "landed.txt";
        var message = $"wip $(touch '{marker}') `touch '{marker}'` it's \"quoted\"";
        scenario.Write(fileName, "landed");
        scenario.Git("stash", "push", "-q", "-u", "-m", message);
        scenario.Commit(fileName, "landed", "land it");
        var stashSha = scenario.StashShas().Single();
        var storedMessage = scenario.StashMessages().Single();
        using var hints = new StringWriter();

        Pruner.Run([scenario.Path], new PruneOptions(Branches: false, Apply: true), hints);
        var restore = hints.ToString().Split("restore with: ")[1].Trim();
        TempRepoFixture.RunProcess("/bin/sh", scenario.Path, "-c", restore);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(marker), Is.False);
            Assert.That(scenario.StashShas(), Is.EqualTo([stashSha]));
            Assert.That(storedMessage, Does.Contain(message));
            Assert.That(scenario.StashMessages().Single(), Is.EqualTo(storedMessage));
        }
    }

    [Test]
    public void LinkedWorktreePath_IsPrunedOnceWithItsMainCheckout()
    {
        using var scenario = CreateWithSquashMergedBranch("feature");
        scenario.Git("branch", "elsewhere");
        var worktree = scenario.AddWorktree("elsewhere");
        using var hints = new StringWriter();

        var report = Pruner.Run([scenario.Path, worktree], new PruneOptions(Stashes: false), hints);

        Assert.That(report.Candidates.Count(c => c.Name == "feature"), Is.EqualTo(1));
    }

    [Test]
    public void InvalidPath_IsIgnored()
    {
        using var directory = new TempDirectory();
        using var hints = new StringWriter();

        var report = Pruner.Run([directory.Path], new PruneOptions(), hints);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Candidates, Is.Empty);
            Assert.That(report.Errors, Is.Empty);
        }
    }
}
