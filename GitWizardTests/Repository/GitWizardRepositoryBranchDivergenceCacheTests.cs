using System.Text.Json;
using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// Verifies per-repository branch divergence caching keyed by (branch tip SHA, comparison tip SHA,
/// base tip SHA), cache invalidation on each key component, shallow-repository bypass, branch
/// deletion pruning, and JSON persistence round-tripping across report refreshes.
/// </summary>
[TestFixture]
public class GitWizardRepositoryBranchDivergenceCacheTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void Refresh_WithUnchangedTips_HitsCacheAndSkipsComputations()
    {
        using var scenario = CreateTwoLocalBranchScenario();
        scenario.NewBranch("merged-local");
        scenario.Commit("merged.txt", "merged", "merged work");
        scenario.Checkout("main");
        scenario.Git("merge", "-q", "--squash", "merged-local");
        scenario.Write("extra.txt", "fixup");
        scenario.Git("add", "extra.txt");
        scenario.Git("commit", "-q", "-m", "squash merged-local with fixup");
        scenario.Git("push", "-q", "origin", "main");

        var repository = new GitWizardRepository(scenario.Path);
        repository.Refresh(allBranches: false);

        var firstComputations = repository.BranchDivergenceComputationsForTesting;
        var firstBranches = SnapshotBranches(repository);
        var firstLocalOnlyCount = repository.LocalOnlyBranchCount;
        var firstOldestDate = repository.OldestLocalOnlyBranchDate;

        repository.Refresh(allBranches: false);

        var secondComputations = repository.BranchDivergenceComputationsForTesting;
        var secondBranches = SnapshotBranches(repository);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstComputations, Is.EqualTo(4), "Cold refresh must compute all four local branches.");
            Assert.That(secondComputations, Is.Zero, "Repeat refresh with unchanged tips must perform zero computations.");
            Assert.That(secondBranches, Is.EqualTo(firstBranches));
            Assert.That(repository.LocalOnlyBranchCount, Is.EqualTo(firstLocalOnlyCount));
            Assert.That(repository.OldestLocalOnlyBranchDate, Is.EqualTo(firstOldestDate));
        }

        // Switching allBranches changes only which branches are listed, still hitting the cache.
        repository.Refresh(allBranches: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.BranchDivergenceComputationsForTesting, Is.Zero,
                "Toggling allBranches must reuse the cached divergence entries without recomputing.");
            Assert.That(repository.Branches!.Select(branch => branch.Name), Does.Contain("main"));
            Assert.That(repository.Branches!.Single(branch => branch.Name == "main").MergedBy, Is.Null);
            Assert.That(repository.LocalOnlyBranchCount, Is.EqualTo(firstLocalOnlyCount));
            Assert.That(repository.OldestLocalOnlyBranchDate, Is.EqualTo(firstOldestDate));
        }
    }

    [Test]
    public void Refresh_WhenBranchTipMoves_RecomputesMovedBranchAndUpdatesCounts()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("feature-1.txt", "one", "first feature commit");
        scenario.Checkout("main");

        var repository = new GitWizardRepository(scenario.Path);
        repository.Refresh(allBranches: true);
        Assert.That(repository.Branches!.Single(branch => branch.Name == "feature").AheadOfDefault, Is.EqualTo(1));

        scenario.Checkout("feature");
        scenario.Commit("feature-2.txt", "two", "second feature commit");
        scenario.Checkout("main");

        repository.Refresh(allBranches: true);
        var feature = repository.Branches!.Single(branch => branch.Name == "feature");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.BranchDivergenceComputationsForTesting, Is.EqualTo(1),
                "Only the branch whose tip moved should miss the cache.");
            Assert.That(feature.AheadOfDefault, Is.EqualTo(2));
            Assert.That(feature.BehindDefault, Is.Zero);
        }
    }

    [Test]
    public void Refresh_WhenComparisonTipMoves_RecomputesAndUpdatesBehindCount()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();
        scenario.NewBranch("feature");
        scenario.Commit("feature.txt", "feature", "feature work");
        scenario.Checkout("main");

        var repository = new GitWizardRepository(scenario.Path);
        repository.Refresh(allBranches: true);
        Assert.That(repository.Branches!.Single(branch => branch.Name == "feature").BehindDefault, Is.Zero);

        // Advance local main (comparisonBranch) without pushing to origin/main (baseBranch).
        scenario.Commit("main-advance.txt", "advance", "advance local main");

        repository.Refresh(allBranches: true);
        var feature = repository.Branches!.Single(branch => branch.Name == "feature");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.BranchDivergenceComputationsForTesting, Is.EqualTo(2),
                "Moving the comparison tip invalidates every branch entry.");
            Assert.That(feature.AheadOfDefault, Is.EqualTo(1));
            Assert.That(feature.BehindDefault, Is.EqualTo(1));
        }
    }

    [Test]
    public void Refresh_WhenBaseTipMoves_RecomputesMergeStateAndLocalOnlyCount()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();
        var initialMainSha = scenario.Sha("main");
        scenario.NewBranch("feature");
        scenario.Commit("feature.txt", "feature content", "feature work");
        scenario.Checkout("main");

        var repository = new GitWizardRepository(scenario.Path);
        repository.Refresh(allBranches: true);

        var featureBefore = repository.Branches!.Single(branch => branch.Name == "feature");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(featureBefore.IsMerged, Is.False);
            Assert.That(featureBefore.MergedBy, Is.Null);
            Assert.That(repository.LocalOnlyBranchCount, Is.EqualTo(1));
        }

        // Land a squash commit on origin/main (baseBranch) while resetting local main
        // (comparisonBranch) back to initialMainSha so only the base tip moves.
        scenario.Git("merge", "-q", "--squash", "feature");
        scenario.Write("extra.txt", "extra");
        scenario.Git("add", "extra.txt");
        scenario.Git("commit", "-q", "-m", "squash feature onto origin");
        scenario.Git("push", "-q", "origin", "main");
        scenario.Git("reset", "-q", "--hard", initialMainSha);

        repository.Refresh(allBranches: true);
        var featureAfter = repository.Branches!.Single(branch => branch.Name == "feature");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.BranchDivergenceComputationsForTesting, Is.EqualTo(2),
                "Moving the base tip invalidates every branch entry.");
            Assert.That(featureAfter.IsMerged, Is.True);
            Assert.That(featureAfter.MergedBy, Is.EqualTo(MergedBy.Content));
            Assert.That(featureAfter.MergedInto, Is.EqualTo("origin/main"));
            Assert.That(repository.LocalOnlyBranchCount, Is.Zero);
        }
    }

    [Test]
    public void Refresh_WhenBranchIsDeleted_PrunesDeletedBranchFromCache()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature-keep");
        scenario.Commit("keep.txt", "keep", "keep commit");
        scenario.Checkout("main");
        scenario.NewBranch("feature-delete");
        scenario.Commit("delete.txt", "delete", "delete commit");
        var deletedTipSha = scenario.Sha("feature-delete");
        scenario.Checkout("main");

        var repository = new GitWizardRepository(scenario.Path);
        repository.Refresh();

        Assert.That(repository.BranchDivergenceCache, Is.Not.Null);
        Assert.That(repository.BranchDivergenceCache!.Select(entry => entry.BranchTipSha),
            Does.Contain(deletedTipSha));

        scenario.Git("branch", "-D", "feature-delete");
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.BranchDivergenceComputationsForTesting, Is.Zero,
                "Remaining branches must hit the cache when another branch is deleted.");
            Assert.That(repository.BranchDivergenceCache, Is.Not.Null);
            Assert.That(repository.BranchDivergenceCache!, Has.Count.EqualTo(2));
            Assert.That(repository.BranchDivergenceCache!.Select(entry => entry.BranchTipSha),
                Does.Not.Contain(deletedTipSha));
        }
    }

    [Test]
    public void ReportRoundTrip_PreservesCacheAndHitsOnRefresh()
    {
        using var directory = new TempDirectory();
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("feature.txt", "feature", "feature work");
        scenario.Checkout("main");

        var report = new GitWizardReport();
        report.Refresh([scenario.Path], allBranches: true);
        var initialRepository = report.Repositories[scenario.Path];
        Assert.That(initialRepository.BranchDivergenceComputationsForTesting, Is.EqualTo(2));

        var reportPath = Path.Combine(directory.Path, "report.json");
        report.Save(reportPath);

        var deserialized = JsonSerializer.Deserialize<GitWizardReport>(File.ReadAllText(reportPath))!;
        Assert.That(deserialized.Repositories[scenario.Path].BranchDivergenceCache, Has.Count.EqualTo(2));

        deserialized.Refresh([scenario.Path], allBranches: true);
        var refreshedRepository = deserialized.Repositories[scenario.Path];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refreshedRepository.BranchDivergenceComputationsForTesting, Is.Zero,
                "Refreshing a deserialized report with unchanged tips must hit the persisted cache.");
            Assert.That(refreshedRepository.Branches!.Single(branch => branch.Name == "feature").AheadOfDefault,
                Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GenerateReportAsync_TwiceWithUnchangedTips_CarriesCacheAndSkipsComputations()
    {
        using var sandbox = new LocalFilesSandbox();
        using var scenario = CreateTwoLocalBranchScenario();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        await using var firstReport = await GitWizardReport.GenerateReportAsync(configuration, [scenario.Path]);
        var firstRepository = firstReport.Repositories[scenario.Path];
        var firstBranches = SnapshotBranches(firstRepository);
        var firstLocalOnlyCount = firstRepository.LocalOnlyBranchCount;
        var firstOldestDate = firstRepository.OldestLocalOnlyBranchDate;

        await firstReport.SaveAsync(GitWizardReport.GetCachedReportPath());

        await using var secondReport = await GitWizardReport.GenerateReportAsync(configuration, [scenario.Path]);
        var secondRepository = secondReport.Repositories[scenario.Path];
        var secondBranches = SnapshotBranches(secondRepository);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstRepository.BranchDivergenceComputationsForTesting, Is.EqualTo(3));
            Assert.That(secondRepository.BranchDivergenceComputationsForTesting, Is.Zero,
                "Second GenerateReportAsync with unchanged tips must carry BranchDivergenceCache and perform zero computations.");
            Assert.That(secondBranches, Is.EqualTo(firstBranches));
            Assert.That(secondRepository.LocalOnlyBranchCount, Is.EqualTo(firstLocalOnlyCount));
            Assert.That(secondRepository.OldestLocalOnlyBranchDate, Is.EqualTo(firstOldestDate));
        }
    }

    [Test]
    public async Task RebuildReport_CarriesBranchDivergenceCacheAndDropsDeletedRepository()
    {
        using var sandbox = new LocalFilesSandbox();
        using var scenario = CreateTwoLocalBranchScenario();
        var deletedScenario = GitScenarioRepo.Create();
        var deletedPath = deletedScenario.Path;
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { scenario.Path, deletedPath },
            IncludeTempDirectories = true
        };
        var runConfiguration = CliParser.Parse(
            ["--rebuild-report", "--no-mft", "--json", "--minified", "--no-local-commit-count"]);

        await using (var firstStdout = new StringWriter())
        {
            await Program.RunDefaultReportCoreAsync(
                runConfiguration, configuration, stdout: firstStdout, stderr: TextWriter.Null);
        }

        var cachedBeforeRebuild = await GitWizardReport.GetCachedReportAsync();
        Assert.That(cachedBeforeRebuild, Is.Not.Null);
        Assert.That(cachedBeforeRebuild!.Repositories.Keys, Does.Contain(deletedPath));

        deletedScenario.Dispose();

        await using var rebuiltReport = await Program.GenerateReportForRunAsync(
            runConfiguration, configuration, null, new UpdateHandler(TextWriter.Null), null);
        var rebuiltRepository = rebuiltReport.Repositories[scenario.Path];

        await using var secondStdout = new StringWriter();
        await Program.RunDefaultReportCoreAsync(
            runConfiguration, configuration, stdout: secondStdout, stderr: TextWriter.Null);
        using var outputJson = JsonDocument.Parse(secondStdout.ToString());
        var outputRepositories = outputJson.RootElement.GetProperty("Repositories");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rebuiltReport.Repositories.ContainsKey(deletedPath), Is.False,
                "Rebuilding the report must drop a repository that no longer exists on disk.");
            Assert.That(outputRepositories.TryGetProperty(deletedPath, out _), Is.False,
                "CLI --rebuild-report output must omit a repository that no longer exists on disk.");
            Assert.That(rebuiltRepository.BranchDivergenceComputationsForTesting, Is.Zero,
                "--rebuild-report must carry BranchDivergenceCache from the previous report.");
            Assert.That(rebuiltRepository.Branches!.Select(branch => branch.Name),
                Is.EquivalentTo(["old-local", "new-local"]));
        }
    }

    [Test]
    public async Task GenerateReportAsync_WhenBranchTipMoves_RecomputesOnlyMovedBranch()
    {
        using var sandbox = new LocalFilesSandbox();
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("stable");
        scenario.Commit("stable.txt", "stable", "stable commit");
        scenario.Checkout("main");
        scenario.NewBranch("feature");
        scenario.Commit("feature-1.txt", "one", "first feature commit");
        scenario.Checkout("main");

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        await using var firstReport = await GitWizardReport.GenerateReportAsync(configuration, [scenario.Path]);
        await firstReport.SaveAsync(GitWizardReport.GetCachedReportPath());

        scenario.Checkout("feature");
        scenario.Commit("feature-2.txt", "two", "second feature commit");
        scenario.Checkout("main");

        await using var secondReport = await GitWizardReport.GenerateReportAsync(configuration, [scenario.Path]);
        var secondRepository = secondReport.Repositories[scenario.Path];
        var feature = secondRepository.Branches!.Single(branch => branch.Name == "feature");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstReport.Repositories[scenario.Path].BranchDivergenceComputationsForTesting, Is.EqualTo(3));
            Assert.That(secondRepository.BranchDivergenceComputationsForTesting, Is.EqualTo(1),
                "Only the moved branch tip must be recomputed after carrying the cache into GenerateReportAsync.");
            Assert.That(feature.AheadOfDefault, Is.EqualTo(2));
            Assert.That(feature.BehindDefault, Is.Zero);
        }
    }

    [Test]
    public void Refresh_WhenRepositoryIsShallow_BypassesCacheUntilUnshallowed()
    {
        using var origin = GitScenarioRepo.Create();
        using var shallowClone = new TempDirectory("gw-shallow-", create: false);
        CreateShallowCloneWithFeatureAtPenultimateCommit(origin, shallowClone.Path);

        var repository = new GitWizardRepository(shallowClone.Path);
        repository.Refresh();

        var featureWhileShallow = repository.Branches!.Single(branch => branch.Name == "feature");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.BranchDivergenceComputationsForTesting, Is.EqualTo(2),
                "Shallow refresh must compute both local branches.");
            Assert.That(repository.BranchDivergenceCache, Is.Null,
                "Shallow refresh must not persist branch divergence cache entries.");
            Assert.That(featureWhileShallow.AheadOfDefault, Is.EqualTo(1));
            Assert.That(featureWhileShallow.BehindDefault, Is.EqualTo(1));
            Assert.That(featureWhileShallow.IsMerged, Is.False);
            Assert.That(featureWhileShallow.MergedBy, Is.Null);
        }

        repository.Refresh();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.BranchDivergenceComputationsForTesting, Is.EqualTo(2),
                "Subsequent refresh of a shallow repository must recompute instead of hitting a cache.");
            Assert.That(repository.BranchDivergenceCache, Is.Null);
        }

        TempRepoFixture.RunGit(shallowClone.Path, "fetch", "-q", "--unshallow");
        repository.Refresh();

        var featureAfterUnshallow = repository.Branches!.Single(branch => branch.Name == "feature");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.BranchDivergenceComputationsForTesting, Is.EqualTo(2),
                "First refresh after unshallowing must recompute against the full commit graph.");
            Assert.That(repository.BranchDivergenceCache, Has.Count.EqualTo(2),
                "Once unshallowed, refresh must persist the branch divergence cache.");
            Assert.That(featureAfterUnshallow.AheadOfDefault, Is.Zero);
            Assert.That(featureAfterUnshallow.BehindDefault, Is.EqualTo(1));
            Assert.That(featureAfterUnshallow.IsMerged, Is.True);
            Assert.That(featureAfterUnshallow.MergedBy, Is.EqualTo(MergedBy.Ancestor));
        }

        repository.Refresh();
        var featureCachedAfterUnshallow = repository.Branches!.Single(branch => branch.Name == "feature");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.BranchDivergenceComputationsForTesting, Is.Zero,
                "Subsequent refresh of an unshallowed repository with unchanged tips must hit the cache.");
            Assert.That(repository.BranchDivergenceCache, Has.Count.EqualTo(2));
            Assert.That(featureCachedAfterUnshallow.AheadOfDefault, Is.Zero);
            Assert.That(featureCachedAfterUnshallow.BehindDefault, Is.EqualTo(1));
            Assert.That(featureCachedAfterUnshallow.MergedBy, Is.EqualTo(MergedBy.Ancestor));
        }
    }

    [Test]
    public void Refresh_WhenDeserializedReportHasCacheForShallowRepository_DiscardsCacheAndRecomputes()
    {
        using var directory = new TempDirectory();
        using var origin = GitScenarioRepo.Create();
        using var shallowClone = new TempDirectory("gw-shallow-", create: false);
        CreateShallowCloneWithFeatureAtPenultimateCommit(origin, shallowClone.Path);
        var mainSha = origin.Sha("main");
        var featureSha = origin.Sha("feature");

        var seededReport = new GitWizardReport();
        seededReport.Repositories[shallowClone.Path] = new GitWizardRepository(shallowClone.Path)
        {
            BranchDivergenceCache =
            [
                new BranchDivergenceCacheEntry
                {
                    BranchTipSha = mainSha,
                    ComparisonTipSha = mainSha,
                    BaseTipSha = mainSha,
                    Ahead = 0,
                    Behind = 0,
                    MergedBy = MergedBy.Ancestor
                },
                new BranchDivergenceCacheEntry
                {
                    BranchTipSha = featureSha,
                    ComparisonTipSha = mainSha,
                    BaseTipSha = mainSha,
                    Ahead = 0,
                    Behind = 1,
                    MergedBy = MergedBy.Ancestor
                }
            ]
        };

        var reportPath = Path.Combine(directory.Path, "report.json");
        seededReport.Save(reportPath);

        var deserialized = JsonSerializer.Deserialize<GitWizardReport>(File.ReadAllText(reportPath))!;
        Assert.That(deserialized.Repositories[shallowClone.Path].BranchDivergenceCache, Has.Count.EqualTo(2));

        deserialized.Refresh([shallowClone.Path], allBranches: true);
        var refreshedRepository = deserialized.Repositories[shallowClone.Path];
        var feature = refreshedRepository.Branches!.Single(branch => branch.Name == "feature");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refreshedRepository.BranchDivergenceCache, Is.Null,
                "Refreshing a shallow repository must discard any cached divergence entries carried from a report.");
            Assert.That(refreshedRepository.BranchDivergenceComputationsForTesting, Is.EqualTo(2),
                "Refreshing a shallow repository must recompute divergence instead of consuming cached entries.");
            Assert.That(feature.AheadOfDefault, Is.EqualTo(1));
            Assert.That(feature.BehindDefault, Is.EqualTo(1));
            Assert.That(feature.IsMerged, Is.False);
            Assert.That(feature.MergedBy, Is.Null);
        }
    }

    static void CreateShallowCloneWithFeatureAtPenultimateCommit(GitScenarioRepo origin, string shallowClonePath)
    {
        origin.Commit("second.txt", "second", "second commit");
        origin.Git("branch", "feature");
        origin.Commit("third.txt", "third", "third commit");

        var originUri = $"file://{origin.Path.Replace('\\', '/')}";
        TempRepoFixture.RunGit(
            TempDirectory.SystemTempPath,
            "clone",
            "-q",
            "--depth=1",
            "--no-single-branch",
            originUri,
            shallowClonePath);
        TempRepoFixture.RunGit(shallowClonePath, "branch", "feature", "origin/feature");
    }

    static GitScenarioRepo CreateTwoLocalBranchScenario()
    {
        var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();
        scenario.NewBranch("old-local");
        scenario.Write("old.txt", "old");
        scenario.Git("add", "old.txt");
        scenario.Git("commit", "-q", "--date", "2020-01-01T00:00:00Z", "-m", "old local work");
        scenario.Checkout("main");
        scenario.NewBranch("new-local");
        scenario.Commit("new.txt", "new", "new local work");
        scenario.Checkout("main");
        return scenario;
    }

    static (string Name, int AheadOfDefault, int BehindDefault, bool IsMerged, string? MergedBy, string? MergedInto, DateTimeOffset? LastCommitDate, bool HasUpstream)[]
        SnapshotBranches(GitWizardRepository repository) =>
        [.. repository.Branches!.Select(branch => (
            branch.Name,
            branch.AheadOfDefault,
            branch.BehindDefault,
            branch.IsMerged,
            branch.MergedBy,
            branch.MergedInto,
            branch.LastCommitDate,
            branch.HasUpstream))];
}
