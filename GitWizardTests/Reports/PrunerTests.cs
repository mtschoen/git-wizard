using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// <see cref="Pruner"/> dry-run and apply behavior, branch deletion safety guards, and stash
/// dropping through archive refs (including injected races and failures), all against throwaway
/// repositories.
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Applied, Is.False);
            Assert.That(report.SchemaVersion, Is.EqualTo("1.1"));
            Assert.That(Find(report, "feature").Action, Is.EqualTo(PruneCandidate.WouldDelete));
            Assert.That(Find(report, "feature").MergedBy, Is.EqualTo(MergedBy.Squash));
            Assert.That(Find(report, "stash@{0}").Action, Is.EqualTo(PruneCandidate.WouldDelete));
            Assert.That(Find(report, "stash@{0}").Kind, Is.EqualTo(PruneCandidate.StashKind));
            Assert.That(Find(report, "main").SkipReason, Is.EqualTo(PruneSkipReasons.BaseBranch));
            Assert.That(scenario.HasBranch("feature"), Is.True);
            Assert.That(scenario.StashShas(), Is.EqualTo(stashesBefore));
            Assert.That(scenario.RefTargets("refs/git-wizard/"), Is.Empty, "A dry run writes no archive refs.");
            Assert.That(hints.ToString(), Is.Empty);
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

    const string ArchivePrefix = "refs/git-wizard/stash-archive/";
    const string DroppedPrefix = "refs/git-wizard/stash-dropped/";

    /// <summary>A merged stash at <c>stash@{1}</c> under an unmerged one at <c>stash@{0}</c>.</summary>
    static GitScenarioRepo CreateWithMergedStashUnderUnmergedStash(out string mergedSha, out string unmergedSha)
    {
        var scenario = GitScenarioRepo.Create();
        StashLandedEdit(scenario, "landed.txt", "landed");
        scenario.Write("base.txt", "never landed");
        scenario.Git("stash", "push", "-q", "-m", "unmerged");
        var shas = scenario.StashShas();
        unmergedSha = shas[0];
        mergedSha = shas[1];
        return scenario;
    }

    static void PushStash(string repositoryPath, string content, string message)
    {
        File.WriteAllText(Path.Combine(repositoryPath, "base.txt"), content);
        TempRepoFixture.RunGit(repositoryPath, "stash", "push", "-q", "-m", message);
    }

    [Test]
    public void Apply_DropsMergedStashesHighestIndexFirstAndKeepsAnUndoWindowUntilTheNextRun()
    {
        using var scenario = GitScenarioRepo.Create();
        StashLandedEdit(scenario, "first.txt", "first");
        scenario.Write("base.txt", "never landed");
        scenario.Git("stash", "push", "-q", "-m", "unmerged");
        StashLandedEdit(scenario, "third.txt", "third");
        var stashes = scenario.StashShas();
        var droppedOrder = new List<string>();
        using var hints = new StringWriter();

        var report = Pruner.Run([scenario.Path], new PruneOptions(Branches: false, Apply: true)
        {
            BeforeDelete = candidate => droppedOrder.Add(candidate.Name)
        }, hints);

        var quotedPath = Pruner.ShellQuote(scenario.Path);
        var hintOutput = hints.ToString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Candidates.Select(candidate => candidate.Action), Is.EqualTo([
                PruneCandidate.Deleted, PruneCandidate.Skipped, PruneCandidate.Deleted
            ]));
            Assert.That(droppedOrder, Is.EqualTo(["stash@{2}", "stash@{0}"]));
            Assert.That(Find(report, "stash@{1}").SkipReason, Is.EqualTo(PruneSkipReasons.NotMerged));
            Assert.That(report.Errors, Is.Empty);
            Assert.That(scenario.StashShas(), Is.EqualTo([stashes[1]]));
            Assert.That(scenario.RefTargets(ArchivePrefix), Is.Empty, "The unmerged stash is still listed, so its archive ref is released.");
            Assert.That(scenario.RefTargets(DroppedPrefix), Is.EquivalentTo(new Dictionary<string, string>
            {
                [DroppedPrefix + stashes[0]] = stashes[0],
                [DroppedPrefix + stashes[2]] = stashes[2]
            }));
            Assert.That(hintOutput, Does.Contain($"Dropped stash@{{2}} ({stashes[2]}); restore with: git -C {quotedPath} stash store -m 'On main: stash first.txt' {stashes[2]}"));
            Assert.That(hintOutput, Does.Contain($"Dropped stash@{{0}} ({stashes[0]}); restore with: git -C {quotedPath} stash store -m 'On main: stash third.txt' {stashes[0]}"));
            Assert.That(hintOutput, Does.Not.Contain(stashes[1]));
        }

        // The undo window survives garbage collection, then the next stash-pruning apply run ends it.
        scenario.Git("gc", "-q", "--prune=now");
        scenario.Git("stash", "store", "-m", "On main: stash first.txt", stashes[2]);
        var nextReport = Pruner.Run([scenario.Path], new PruneOptions(Branches: false, Apply: true, OnlyShas: new HashSet<string>()), TextWriter.Null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nextReport.Candidates.All(candidate => candidate.SkipReason == PruneSkipReasons.NotConfirmed), Is.True);
            Assert.That(scenario.StashShas(), Is.EqualTo([stashes[2], stashes[1]]));
            Assert.That(scenario.RefTargets(DroppedPrefix), Is.Empty);
            Assert.That(scenario.RefTargets(ArchivePrefix), Is.Empty);
        }
    }

    [Test]
    public void Apply_ConcurrentStashPushBeforeDrop_KeepsTheUnmergedStashListed()
    {
        using var scenario = CreateWithMergedStashUnderUnmergedStash(out var mergedSha, out var unmergedSha);
        var scenarioPath = scenario.Path;
        using var hints = new StringWriter();
        var options = new PruneOptions(Branches: false, Apply: true)
        {
            BeforeDelete = _ => PushStash(scenarioPath, "pushed during prune", "concurrent")
        };

        var report = Pruner.Run([scenario.Path], options, hints);

        var stashes = scenario.StashShas();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stashes, Does.Contain(unmergedSha), "The drop hit the unmerged stash; it must be stored back.");
            Assert.That(stashes, Does.Contain(mergedSha));
            Assert.That(stashes, Has.Count.EqualTo(3));
            Assert.That(Find(report, "stash@{1}").Action, Is.EqualTo(PruneCandidate.Skipped));
            Assert.That(Find(report, "stash@{1}").SkipReason, Is.EqualTo(PruneSkipReasons.StashMismatch));
            Assert.That(report.Errors, Is.Empty);
            Assert.That(scenario.RefTargets(ArchivePrefix), Is.Empty);
            Assert.That(hints.ToString(), Does.Not.Contain("Dropped"));
        }
    }

    [Test]
    public void Apply_StashStoreFailsAfterMismatchedDrop_KeepsTheArchiveRefAndReportsIt()
    {
        using var scenario = CreateWithMergedStashUnderUnmergedStash(out var mergedSha, out var unmergedSha);
        var scenarioPath = scenario.Path;
        using var hints = new StringWriter();
        var options = new PruneOptions(Branches: false, Apply: true)
        {
            BeforeDelete = _ =>
            {
                PushStash(scenarioPath, "pushed during prune", "concurrent");
                GitScenarioRepo.RejectStashRefUpdates(scenarioPath);
            }
        };

        var report = Pruner.Run([scenario.Path], options, hints);

        var archiveRef = ArchivePrefix + unmergedSha;
        var stashes = scenario.StashShas();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.RefTargets(ArchivePrefix), Is.EquivalentTo(new Dictionary<string, string> { [archiveRef] = unmergedSha }));
            Assert.That(stashes, Does.Not.Contain(unmergedSha), "The injected hook makes the restore fail.");
            Assert.That(stashes, Does.Contain(mergedSha));
            Assert.That(report.Errors.Single().Error, Does.Contain(archiveRef));
            Assert.That(Find(report, "stash@{1}").SkipReason, Is.EqualTo(PruneSkipReasons.StashMismatch));
            Assert.That(hints.ToString(), Does.Contain($"Stash {unmergedSha} is not in the stash list; {archiveRef} keeps it."));
        }

        // A later dry run repeats the hint, and the hint's command restores the stash.
        File.Delete(Path.Combine(scenario.Path, ".git", "hooks", "reference-transaction"));
        using var dryRunHints = new StringWriter();
        Pruner.Run([scenario.Path], new PruneOptions(Branches: false), dryRunHints);
        scenario.Git("stash", "store", "-m", "On main: unmerged", unmergedSha);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dryRunHints.ToString(), Does.Contain($"git -C {Pruner.ShellQuote(scenario.Path)} stash store -m 'On main: unmerged' {unmergedSha}"));
            Assert.That(scenario.StashShas(), Does.Contain(unmergedSha));
        }
    }

    [Test]
    public void Apply_UnparseableStashDropOutput_IsAMismatchAndTheStashIsStoredBack()
    {
        using var scenario = CreateWithMergedStashUnderUnmergedStash(out var mergedSha, out var unmergedSha);
        using var hints = new StringWriter();
        var options = new PruneOptions(Branches: false, Apply: true)
        {
            AfterStashDrop = result => result with { Output = "Dropped something git never prints\n" }
        };

        var report = Pruner.Run([scenario.Path], options, hints);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Find(report, "stash@{1}").Action, Is.EqualTo(PruneCandidate.Skipped));
            Assert.That(Find(report, "stash@{1}").SkipReason, Is.EqualTo(PruneSkipReasons.StashMismatch));
            Assert.That(scenario.StashShas(), Is.EquivalentTo([mergedSha, unmergedSha]));
            Assert.That(scenario.StashMessages(), Does.Contain("On main: stash landed.txt"));
            Assert.That(report.Errors, Is.Empty);
            Assert.That(scenario.RefTargets(ArchivePrefix), Is.Empty);
            Assert.That(hints.ToString(), Does.Not.Contain("Dropped stash@"));
        }
    }

    [TestCase("Dropped refs/stash@{0} (1b1d5f12126c02c9bec4dd13b428b3374df3dea0)\n", "1b1d5f12126c02c9bec4dd13b428b3374df3dea0")]
    [TestCase("Dropped stash@{0} (1b1d5f1)", null)]
    [TestCase("Dropped stash@{0} (1B1D5F12126C02C9BEC4DD13B428B3374DF3DEA0)", null)]
    [TestCase("", null)]
    public void ParseDroppedStashSha_AcceptsOnlyAFullLowercaseObjectId(string output, string? expected) =>
        Assert.That(Pruner.ParseDroppedStashSha(output), Is.EqualTo(expected));

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
