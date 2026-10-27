using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// <see cref="Pruner"/> dry-run and apply behavior, branch deletion safety guards, and report-only
/// stash handling, all against throwaway repositories.
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

    static PruneCandidate Find(PruneReport report, string name) => report.Candidates.Single(candidate => candidate.Name == name);

    [Test]
    public void DryRun_ReportsWouldDeleteAndDeletesNothing()
    {
        using var scenario = CreateWithSquashMergedBranch("feature");
        StashLandedEdit(scenario, "stashed.txt", "stashed");
        var stashesBefore = scenario.StashShas();
        using var hints = new StringWriter();

        var report = Pruner.Run([scenario.Path], new PruneOptions(), hints);

        var quotedPath = Pruner.ShellQuote(scenario.Path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Applied, Is.False);
            Assert.That(report.SchemaVersion, Is.EqualTo("1.0"));
            Assert.That(Find(report, "feature").Action, Is.EqualTo(PruneCandidate.WouldDelete));
            Assert.That(Find(report, "feature").MergedBy, Is.EqualTo(MergedBy.Squash));
            Assert.That(Find(report, "stash@{0}").Action, Is.EqualTo(PruneCandidate.ManualDrop));
            Assert.That(Find(report, "stash@{0}").Kind, Is.EqualTo(PruneCandidate.StashKind));
            Assert.That(Find(report, "main").SkipReason, Is.EqualTo(PruneSkipReasons.BaseBranch));
            Assert.That(scenario.HasBranch("feature"), Is.True);
            Assert.That(scenario.StashShas(), Is.EqualTo(stashesBefore));
            Assert.That(hints.ToString(), Does.Contain($"git -C {quotedPath} stash list --format='%H %gd' | grep '^{stashesBefore[0]} '")
                .And.Contain($"git -C {quotedPath} stash drop stash@{{0}}"));
        }
    }

    [TestCase("default")]
    [TestCase("pr439-branch-tip-advanced")]
    [TestCase("pr439-checkout-after-inspection")]
    [TestCase("pr439-worktree-after-inspection")]
    [TestCase("pr439-symbolic-ref-substituted")]
    public void Apply_DeletesMergedBranchAndSkipsCheckedOutAndWorktreeBranches(string mode)
    {
        using var scenario = CreateWithSquashMergedBranch("merged");
        scenario.NewBranch("unmerged");
        scenario.Commit("unmerged.txt", "unmerged", "unmerged");
        var unmergedSha = scenario.Sha("unmerged");
        scenario.Checkout("main");
        if (mode == "pr439-symbolic-ref-substituted")
        {
            scenario.Git("branch", "-f", "merged", "main");
        }

        scenario.Git("symbolic-ref", "refs/heads/symbolic-alias", "refs/heads/main");
        scenario.Git("branch", "in-worktree");
        scenario.Git("branch", "checked-out");
        scenario.AddWorktree("in-worktree");
        scenario.Checkout("checked-out");
        var mergedSha = scenario.Sha("merged");
        var scenarioPath = scenario.Path;
        using var lateWorktree = new TempDirectory("gw-scenario-wt-", create: false);
        var lateWorktreePath = lateWorktree.Path;
        using var hints = new StringWriter();

        var options = new PruneOptions(Stashes: false, Apply: true)
        {
            BeforeDelete = candidate =>
            {
                if (candidate.Name != "merged")
                {
                    return;
                }

                switch (mode)
                {
                    case "pr439-branch-tip-advanced":
                        TempRepoFixture.RunGit(scenarioPath, "update-ref", "refs/heads/merged", unmergedSha);
                        break;
                    case "pr439-checkout-after-inspection":
                        TempRepoFixture.RunGit(scenarioPath, "checkout", "-q", "merged");
                        break;
                    case "pr439-worktree-after-inspection":
                        TempRepoFixture.RunGit(scenarioPath, "worktree", "add", "-q", lateWorktreePath, "merged");
                        break;
                    case "pr439-symbolic-ref-substituted":
                        TempRepoFixture.RunGit(scenarioPath, "symbolic-ref", "refs/heads/merged", "refs/heads/main");
                        break;
                }
            }
        };
        var report = Pruner.Run([scenario.Path], options, hints);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Applied, Is.True);
            if (mode == "pr439-branch-tip-advanced")
            {
                Assert.That(Find(report, "merged").Action, Is.EqualTo(PruneCandidate.Skipped));
                Assert.That(Find(report, "merged").SkipReason, Is.EqualTo(PruneSkipReasons.DeleteFailed));
                Assert.That(scenario.HasBranch("merged"), Is.True);
                Assert.That(scenario.Sha("merged"), Is.EqualTo(unmergedSha));
                Assert.That(hints.ToString(), Is.Empty);
            }
            else if (mode is "pr439-checkout-after-inspection" or "pr439-worktree-after-inspection")
            {
                Assert.That(Find(report, "merged").Action, Is.EqualTo(PruneCandidate.Skipped));
                Assert.That(Find(report, "merged").SkipReason, Is.EqualTo(PruneSkipReasons.CheckedOut));
                Assert.That(scenario.HasBranch("merged"), Is.True);
                Assert.That(hints.ToString(), Is.Empty);
            }
            else
            {
                Assert.That(Find(report, "merged").Action, Is.EqualTo(PruneCandidate.Deleted));
                Assert.That(scenario.HasBranch("merged"), Is.False);
                Assert.That(hints.ToString(), Does.Contain($"branch 'merged' {mergedSha}"));
            }

            Assert.That(Find(report, "symbolic-alias").Action, Is.EqualTo(PruneCandidate.Skipped));
            Assert.That(Find(report, "symbolic-alias").SkipReason, Is.EqualTo(PruneSkipReasons.SymbolicRef));
            Assert.That(Find(report, "checked-out").SkipReason, Is.EqualTo(PruneSkipReasons.CheckedOut));
            Assert.That(Find(report, "in-worktree").SkipReason, Is.EqualTo(PruneSkipReasons.CheckedOutInWorktree));
            Assert.That(Find(report, "unmerged").SkipReason, Is.EqualTo(PruneSkipReasons.NotMerged));
            Assert.That(Find(report, "unmerged").MergedBy, Is.Null);
            Assert.That(Find(report, "main").SkipReason, Is.EqualTo(PruneSkipReasons.BaseBranch));
            Assert.That(scenario.HasBranch("main"), Is.True);
            Assert.That(scenario.HasBranch("checked-out") && scenario.HasBranch("in-worktree") && scenario.HasBranch("unmerged"), Is.True);
            Assert.That(report.Candidates.Any(candidate => candidate.Kind == PruneCandidate.StashKind), Is.False);
        }
    }

    [Test]
    public void Apply_ReportsMergedStashesForManualDropAndLeavesEveryStashInPlace()
    {
        using var scenario = GitScenarioRepo.Create();
        StashLandedEdit(scenario, "first.txt", "first");
        scenario.Write("base.txt", "never landed");
        scenario.Git("stash", "push", "-q", "-m", "unmerged");
        StashLandedEdit(scenario, "third.txt", "third");
        var stashes = scenario.StashShas();
        using var hints = new StringWriter();

        var report = Pruner.Run([scenario.Path], new PruneOptions(Branches: false, Apply: true), hints);

        var quotedPath = Pruner.ShellQuote(scenario.Path);
        var hintOutput = hints.ToString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Candidates.Select(candidate => candidate.Action), Is.EqualTo([
                PruneCandidate.ManualDrop, PruneCandidate.Skipped, PruneCandidate.ManualDrop
            ]));
            Assert.That(Find(report, "stash@{1}").SkipReason, Is.EqualTo(PruneSkipReasons.NotMerged));
            Assert.That(scenario.StashShas(), Is.EqualTo(stashes));
            Assert.That(hintOutput, Does.Contain($"git -C {quotedPath} stash list --format='%H %gd' | grep '^{stashes[0]} '")
                .And.Contain($"git -C {quotedPath} stash drop stash@{{0}}"));
            Assert.That(hintOutput, Does.Contain($"git -C {quotedPath} stash list --format='%H %gd' | grep '^{stashes[2]} '")
                .And.Contain($"git -C {quotedPath} stash drop stash@{{2}}"));
            Assert.That(hintOutput, Does.Not.Contain(stashes[1]));
            Assert.That(report.Candidates.Any(candidate => candidate.Kind == PruneCandidate.BranchKind), Is.False);
        }
    }

    [Test]
    public void Apply_RestoreHint_IsShellSafeForSingleQuoteInBranchName()
    {
        using var scenario = CreateWithSquashMergedBranch("it's-merged");
        var sha = scenario.Sha("it's-merged");
        using var hints = new StringWriter();

        Pruner.Run([scenario.Path], new PruneOptions(Stashes: false, Apply: true), hints);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.HasBranch("it's-merged"), Is.False);
            Assert.That(hints.ToString(), Does.Contain($"branch 'it'\\''s-merged' {sha}"));
        }
    }

    [TestCase("exact")]
    [TestCase("pr439-windows-case-variants")]
    public void LinkedWorktreePath_IsPrunedOnceWithItsMainCheckout(string mode)
    {
        using var scenario = CreateWithSquashMergedBranch("feature");
        scenario.Git("branch", "elsewhere");
        var worktree = scenario.AddWorktree("elsewhere");
        using var hints = new StringWriter();
        var paths = mode == "pr439-windows-case-variants" && OperatingSystem.IsWindows()
            ? new[] { scenario.Path.ToLowerInvariant(), scenario.Path.ToUpperInvariant(), worktree.ToUpperInvariant() }
            : new[] { scenario.Path, worktree };

        var report = Pruner.Run(paths, new PruneOptions(Stashes: false), hints);

        Assert.That(report.Candidates.Count(candidate => candidate.Name == "feature"), Is.EqualTo(1));
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
