using GitWizard;
using GitWizardTests.TestSupport;
using static GitWizardTests.PrunerTests;

namespace GitWizardTests;

/// <summary>
/// <see cref="Pruner"/> stash dropping through archive refs, including injected races, overlapping
/// prunes, interruptions and failures, against throwaway repositories.
/// </summary>
[TestFixture]
public class PrunerStashTests
{
    const string ArchivePrefix = "refs/git-wizard/stash-archive/";
    const string DroppedPrefix = "refs/git-wizard/stash-dropped/";

    /// <summary>The archive refs expected for <paramref name="shas"/>, keyed by ref name.</summary>
    static Dictionary<string, string> ArchiveRefsFor(params string[] shas) =>
        shas.ToDictionary(sha => ArchivePrefix + sha, sha => sha, StringComparer.Ordinal);

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
            Assert.That(scenario.RefTargets(ArchivePrefix), Is.EquivalentTo(ArchiveRefsFor(stashes[1])), "Only a confirmed drop releases an archive ref.");
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
            Assert.That(scenario.RefTargets(ArchivePrefix), Is.EquivalentTo(ArchiveRefsFor(stashes[1])));
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
            Assert.That(scenario.RefTargets(ArchivePrefix), Is.EquivalentTo(ArchiveRefsFor(mergedSha, unmergedSha)), "The concurrent push was never dropped, so it needs no archive ref.");
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
            Assert.That(scenario.RefTargets(ArchivePrefix), Is.EquivalentTo(ArchiveRefsFor(unmergedSha, mergedSha)));
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
            Assert.That(scenario.RefTargets(ArchivePrefix), Is.EquivalentTo(ArchiveRefsFor(mergedSha, unmergedSha)));
            Assert.That(hints.ToString(), Does.Not.Contain("Dropped stash@"));
        }
    }

    [Test]
    public async Task Apply_OverlappingPruneInterruptedAfterAStaleDrop_KeepsTheOriginalUnmergedStashAnchored()
    {
        using var scenario = CreateWithMergedStashUnderUnmergedStash(out var mergedSha, out var unmergedSha);
        var scenarioPath = scenario.Path;
        var otherPruneAtDrop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPruneFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PruneReport>? otherPrune = null;

        // The other prune starts once this one has archived [unmerged, merged]: it accepts those
        // archive refs, targets the merged stash at index 1, and waits there until this prune has
        // finished. It is then interrupted right after its drop, before any restore.
        var otherOptions = new PruneOptions(Branches: false, Apply: true)
        {
            BeforeDelete = _ =>
            {
                otherPruneAtDrop.SetResult();
                if (!firstPruneFinished.Task.Wait(TestTimeouts.HangGuard))
                {
                    throw new TimeoutException("The first prune did not finish.");
                }
            },
            AfterStashDrop = _ => throw new InvalidOperationException("Interrupted after the stash drop")
        };
        var firstOptions = new PruneOptions(Branches: false, Apply: true)
        {
            BeforeDelete = _ =>
            {
                otherPrune = Task.Run(() => Pruner.Run([scenarioPath], otherOptions, TextWriter.Null));
                if (!otherPruneAtDrop.Task.Wait(TestTimeouts.HangGuard))
                {
                    throw new TimeoutException("The other prune did not reach its drop.");
                }
            }
        };

        var firstReport = Pruner.Run([scenario.Path], firstOptions, TextWriter.Null);

        // A new push shifts the other prune's stale index 1 onto the original unmerged stash.
        PushStash(scenario.Path, "pushed after the first prune", "late push");
        firstPruneFinished.SetResult();
        Assert.That(otherPrune, Is.Not.Null);
        var otherReport = await otherPrune!.WaitAsync(TestTimeouts.HangGuard);
        scenario.Git("gc", "-q", "--prune=now");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Find(firstReport, "stash@{1}").Action, Is.EqualTo(PruneCandidate.Deleted));
            Assert.That(otherReport.Errors.Single().Error, Does.Contain("Interrupted after the stash drop"));
            Assert.That(scenario.StashShas(), Does.Not.Contain(unmergedSha), "The other prune's stale drop removed it.");
            Assert.That(scenario.RefTargets(ArchivePrefix), Does.ContainKey(ArchivePrefix + unmergedSha));
            Assert.That(scenario.HasObject(unmergedSha), Is.True, "The original unmerged stash lost its only anchor.");
            Assert.That(scenario.RefTargets(DroppedPrefix), Is.EquivalentTo(new Dictionary<string, string> { [DroppedPrefix + mergedSha] = mergedSha }));
        }
    }

    [Test]
    public void Apply_NewlyPushedStashIsTheOneDropped_IsStoredBackAndKeepsItsArchiveRef()
    {
        using var scenario = GitScenarioRepo.Create();
        StashLandedEdit(scenario, "landed.txt", "landed");
        var mergedSha = scenario.StashShas().Single();
        var scenarioPath = scenario.Path;
        string? pushedSha = null;
        using var hints = new StringWriter();
        var options = new PruneOptions(Branches: false, Apply: true)
        {
            // Pushed after the last list, so the drop of stash@{0} removes it, not the candidate.
            BeforeDelete = _ =>
            {
                PushStash(scenarioPath, "pushed during prune", "concurrent");
                pushedSha = GitScenarioRepo.TopStashSha(scenarioPath);
            }
        };

        var report = Pruner.Run([scenario.Path], options, hints);

        Assert.That(pushedSha, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.StashShas(), Is.EqualTo([pushedSha, mergedSha]));
            Assert.That(scenario.StashMessages()[0], Is.EqualTo("On main: concurrent"));
            Assert.That(Find(report, "stash@{0}").SkipReason, Is.EqualTo(PruneSkipReasons.StashMismatch));
            Assert.That(report.Errors, Is.Empty);
            Assert.That(scenario.RefTargets(ArchivePrefix), Is.EquivalentTo(ArchiveRefsFor(mergedSha, pushedSha!)), "Archive refs stay unless this run confirmed the drop.");
            Assert.That(hints.ToString(), Does.Not.Contain("Dropped stash@"));
        }
    }

    [Test]
    public void Apply_NewlyPushedStashDroppedAndTheStashListThenFails_IsAnchoredAndReported()
    {
        using var scenario = GitScenarioRepo.Create();
        StashLandedEdit(scenario, "landed.txt", "landed");
        var scenarioPath = scenario.Path;
        string? pushedSha = null;
        var options = new PruneOptions(Branches: false, Apply: true)
        {
            BeforeDelete = _ =>
            {
                PushStash(scenarioPath, "pushed during prune", "concurrent");
                pushedSha = GitScenarioRepo.TopStashSha(scenarioPath);
            },
            // An invalid log.date makes every later "git stash list" fail, so the mismatch path
            // never restores the dropped stash: only its archive ref can keep it.
            AfterStashDrop = result =>
            {
                TempRepoFixture.RunGit(scenarioPath, "config", "log.date", "invalid-date-format");
                return result;
            }
        };

        var report = Pruner.Run([scenario.Path], options, TextWriter.Null);

        scenario.Git("config", "--unset", "log.date");
        scenario.Git("gc", "-q", "--prune=now");
        Assert.That(pushedSha, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.StashShas(), Does.Not.Contain(pushedSha));
            Assert.That(scenario.RefTargets(ArchivePrefix), Does.ContainKey(ArchivePrefix + pushedSha));
            Assert.That(scenario.HasObject(pushedSha!), Is.True, "The dropped new arrival had no anchor.");
            Assert.That(report.Errors.Single().Error, Does.Contain(ArchivePrefix + pushedSha));
            Assert.That(Find(report, "stash@{0}").SkipReason, Is.EqualTo(PruneSkipReasons.StashMismatch));
        }
    }

    [Test]
    public void Apply_UnidentifiedDropOfANewlyPushedStash_ReportsWhereToFindIt()
    {
        using var scenario = GitScenarioRepo.Create();
        StashLandedEdit(scenario, "landed.txt", "landed");
        var mergedSha = scenario.StashShas().Single();
        var scenarioPath = scenario.Path;
        var options = new PruneOptions(Branches: false, Apply: true)
        {
            BeforeDelete = _ => PushStash(scenarioPath, "pushed during prune", "concurrent"),
            AfterStashDrop = result => result with { Output = "Dropped something git never prints\n" }
        };

        var report = Pruner.Run([scenario.Path], options, TextWriter.Null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.StashShas(), Is.EqualTo([mergedSha]), "The drop removed the unnamed new arrival.");
            Assert.That(Find(report, "stash@{0}").SkipReason, Is.EqualTo(PruneSkipReasons.StashMismatch));
            Assert.That(report.Errors.Single().Error, Does.Contain("fsck --unreachable"));
        }
    }

    [TestCase("Dropped refs/stash@{0} (1b1d5f12126c02c9bec4dd13b428b3374df3dea0)\n", "1b1d5f12126c02c9bec4dd13b428b3374df3dea0")]
    [TestCase("Dropped stash@{0} (1b1d5f1)", null)]
    [TestCase("Dropped stash@{0} (1B1D5F12126C02C9BEC4DD13B428B3374DF3DEA0)", null)]
    [TestCase("", null)]
    public void ParseDroppedStashSha_AcceptsOnlyAFullLowercaseObjectId(string output, string? expected) =>
        Assert.That(Pruner.ParseDroppedStashSha(output), Is.EqualTo(expected));
}
