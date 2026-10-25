using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

// Report tests exercise report generation, aggregation contracts, and refresh orchestration.
public class GitWizardReportTests
{
    [Test]
    public void Report_HasSchemaVersion()
    {
        var report = new GitWizardReport();
        Assert.That(report.SchemaVersion, Is.EqualTo("2.1"));
    }

    [Test]
    public void Report_SchemaVersionSerializesToJson()
    {
        var report = new GitWizardReport();
        var json = System.Text.Json.JsonSerializer.Serialize(report);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<GitWizardReport>(json);
        Assert.That(deserialized!.SchemaVersion, Is.EqualTo("2.1"));
    }

    [TestCase(null, "actionable")]
    [TestCase(false, "actionable")]
    [TestCase(true, "all")]
    public async Task GeneratedReport_SetsExpectedBranchScope(bool? allBranches, string expectedScope)
    {
        GitWizardLog.SilentMode = true;
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var options = allBranches.HasValue ? new GitWizardReportOptions { AllBranches = allBranches.Value } : null;
        var report = await GitWizardReport.GenerateReportAsync(configuration, new List<string>(), options: options);

        Assert.Multiple(() =>
        {
            Assert.That(report.BranchScope, Is.EqualTo(expectedScope));
            Assert.That(report.SchemaVersion, Is.EqualTo("2.1"));
            Assert.That(report.Repositories, Is.Empty);
        });
    }

    [Test]
    public void BranchScope_PropertySetters_Work()
    {
        var report = new GitWizardReport();
        Assert.That(report.BranchScope, Is.Null);
        report.BranchScope = "actionable";
        Assert.That(report.BranchScope, Is.EqualTo("actionable"));
        report.BranchScope = "all";
        Assert.That(report.BranchScope, Is.EqualTo("all"));
    }

    [Test]
    public void DefaultCtor_InitializesCollectionsAndProperties()
    {
        var report = new GitWizardReport();
        Assert.Multiple(() =>
        {
            Assert.That(report.SearchPaths, Is.InstanceOf<SortedSet<string>>().And.Empty);
            Assert.That(report.IgnoredPaths, Is.InstanceOf<SortedSet<string>>().And.Empty);
            Assert.That(report.Repositories, Is.InstanceOf<SortedDictionary<string, GitWizardRepository>>().And.Empty);
            Assert.That(report.DeletedPaths, Is.InstanceOf<HashSet<string>>().And.Empty);
            Assert.That(report.NonRepositoryPaths, Is.InstanceOf<HashSet<string>>().And.Empty);
            Assert.That(report.BranchScope, Is.Null);
            Assert.That(report.SchemaVersion, Is.EqualTo("2.1"));
        });
    }

    [Test]
    public void Constructor_FromConfiguration_CopiesSearchAndIgnoredPaths()
    {
        var config = new GitWizardConfiguration
        {
            SearchPaths = { "/search1", "/search2" },
            IgnoredPaths = { "/ignore1" }
        };
        var report = new GitWizardReport(config);
        Assert.Multiple(() =>
        {
            Assert.That(report.SearchPaths, Is.EqualTo(["/search1", "/search2"]));
            Assert.That(report.IgnoredPaths, Is.EqualTo(["/ignore1"]));
        });
    }

    [Test]
    public async Task GenerateReportAsync_WithRealRepo_PopulatesRepository()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = await GitWizardReport.GenerateReportAsync(config, new List<string> { fixture.Path });

        Assert.That(report.Repositories, Has.Count.EqualTo(1));
        Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
    }

    [Test]
    public async Task GenerateReportAsync_NullPaths_TriesDiscovery()
    {
        GitWizardLog.SilentMode = true;
        var config = new GitWizardConfiguration
        {
            SearchPaths = [],
            IgnoredPaths = []
        };
        var options = new GitWizardReportOptions { NoMft = true };
        var report = await GitWizardReport.GenerateReportAsync(config, options: options);

        Assert.That(report, Is.Not.Null);
        Assert.That(report.Repositories, Is.Empty);
    }

    [Test]
    public void Refresh_WithUpdateHandler_CallsStartProgressAndUpdateProgress()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(config);
        var handler = new RecordingUpdateHandler();

        report.Refresh(new SortedSet<string> { fixture.Path }, handler);

        Assert.Multiple(() =>
        {
            Assert.That(handler.ProgressStarts, Is.Not.Empty);
            Assert.That(handler.ProgressCounts, Is.Not.Empty);
            Assert.That(handler.RepositoriesCreated.Length, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Refresh_EmptyPaths_DoesNotThrow()
    {
        GitWizardLog.SilentMode = true;
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(config);
        Assert.DoesNotThrow(() => report.Refresh(new SortedSet<string>()));
    }

    [Test]
    public void Refresh_MultipleNonRepoPaths_AllTracked()
    {
        GitWizardLog.SilentMode = true;
        using var directory1 = new TempDirectory("gw-nonrepo1-");
        using var directory2 = new TempDirectory("gw-nonrepo2-");
        var dir1 = directory1.Path;
        var dir2 = directory2.Path;

        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(config);
        report.Refresh(new SortedSet<string> { dir1, dir2 });

        Assert.That(report.NonRepositoryPaths, Has.Count.EqualTo(2));
    }

    [Test]
    public void RefreshSingleRepository_SetsRefreshTimeSeconds()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(config);

        report.Refresh(new SortedSet<string> { fixture.Path });

        var repo = report.Repositories[fixture.Path];
        Assert.That(repo.RefreshTimeSeconds, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void GetRepositoryPaths_Enumerable_ReturnsAllKeys()
    {
        var report = new GitWizardReport();
        report.Repositories["/a"] = new GitWizardRepository("/a");
        report.Repositories["/b"] = new GitWizardRepository("/b");
        var keys = report.GetRepositoryPaths().ToList();
        Assert.That(keys, Is.EqualTo(["/a", "/b"]));
    }


    [Test]
    [Repeat(20)]
    public void Refresh_ConcurrentCallsOnOneReport_DoNotCorruptRepositories()
    {
        GitWizardLog.SilentMode = true;
        using var repoA = TempRepoFixture.CreateWithInitialCommit();
        using var repoB = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        // Seed paths directly from temp repos (like the sibling Refresh_* tests) instead of
        // running discovery - discovery would trigger MFT/UAC on Windows, and the subject under
        // test here is parallel Refresh, not discovery.
        var repositoryPaths = new SortedSet<string> { repoA.Path, repoB.Path };
        Parallel.For(0, 10, _ => report.Refresh(repositoryPaths));

        // Ten concurrent full refreshes over the same two paths race unsynchronized
        // dictionary mutation (PruneDeletedRepositories, PruneNonRepositoryPaths) against
        // each other and against RefreshSingleRepository's own locked writes. A corrupted
        // SortedDictionary can end up missing entries, holding a stale null, or throwing
        // mid-enumeration; assert the dictionary settles into exactly the seeded paths with
        // every value populated.
        Assert.That(report.Repositories.Count, Is.EqualTo(repositoryPaths.Count));
        Assert.That(report.Repositories.Values, Has.All.Not.Null);
    }

    [Test]
    public void Refresh_DeletedRepo_TracksDeletedPath()
    {
        GitWizardLog.SilentMode = true;
        using var tempRepo = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { tempRepo.Path };
        report.Refresh(paths);
        Assert.That(report.DeletedPaths.Count, Is.EqualTo(0));
        Assert.That(report.Repositories.ContainsKey(tempRepo.Path), Is.True);

        // Delete the repo directory
        tempRepo.DeleteNow();

        // Refresh with the deleted path
        report.Repositories.Clear();
        report.Refresh(paths);

        Assert.That(report.DeletedPaths.Count, Is.EqualTo(1));
        Assert.That(report.DeletedPaths.Contains(tempRepo.Path), Is.True);
        Assert.That(report.Repositories.ContainsKey(tempRepo.Path), Is.False);
    }

    [Test]
    public void Refresh_MixedDeletedAndValid_Repos()
    {
        GitWizardLog.SilentMode = true;
        using var validRepo = TempRepoFixture.CreateWithInitialCommit();
        using var deleted = new TempDirectory("nonexistent-repo-", create: false);
        var deletedPath = deleted.Path;

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { validRepo.Path, deletedPath };
        report.Refresh(paths);

        Assert.That(report.DeletedPaths.Count, Is.EqualTo(1));
        Assert.That(report.DeletedPaths.Contains(deletedPath), Is.True);
        Assert.That(report.Repositories.ContainsKey(validRepo.Path), Is.True);
    }

    [Test]
    public void Refresh_NoDeletedRepos_EmptyDeletedPaths()
    {
        GitWizardLog.SilentMode = true;
        using var validRepo = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { validRepo.Path };
        report.Refresh(paths);

        Assert.That(report.DeletedPaths.Count, Is.EqualTo(0));
    }

    [Test]
    public void Refresh_NonRepoPath_TracksAndPrunesNonRepositoryPath()
    {
        GitWizardLog.SilentMode = true;
        using var nonRepo = new TempDirectory("not-a-repo-");
        var nonRepoPath = nonRepo.Path;

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { nonRepoPath };
        report.Refresh(paths);

        Assert.That(report.NonRepositoryPaths.Count, Is.EqualTo(1));
        Assert.That(report.NonRepositoryPaths.Contains(nonRepoPath), Is.True);
        Assert.That(report.Repositories.ContainsKey(nonRepoPath), Is.False);

        // A merely-missing directory must NOT be mistaken for a stale non-repo path.
        Assert.That(report.DeletedPaths, Does.Not.Contain(nonRepoPath));
    }

    [Test]
    public void Refresh_RecoveredPath_IsNotPrunedAsNonRepository()
    {
        GitWizardLog.SilentMode = true;
        using var directory = new TempDirectory("recovers-repo-");
        var path = directory.Path;

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);
        report.Repositories[path] = new GitWizardRepository(path)
        {
            RefreshError = GitWizardRepository.NotAGitRepositoryError
        };

        Repository.Init(path);

        var paths = new SortedSet<string> { path };
        report.Refresh(paths);

        Assert.That(report.Repositories.ContainsKey(path), Is.True);
        Assert.That(report.NonRepositoryPaths, Does.Not.Contain(path));
    }

    [Test]
    public void Refresh_ValidRepo_EmptyNonRepositoryPaths()
    {
        GitWizardLog.SilentMode = true;
        using var validRepo = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { validRepo.Path };
        report.Refresh(paths);

        Assert.That(report.NonRepositoryPaths, Is.Empty);
    }

    [Test]
    public void Refresh_DeletedRepos_RemovedFromReportDictionary()
    {
        GitWizardLog.SilentMode = true;
        using var tempRepo = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { tempRepo.Path };
        report.Refresh(paths);
        Assert.That(report.Repositories.ContainsKey(tempRepo.Path), Is.True);

        // Delete the repo
        tempRepo.DeleteNow();

        // Refresh again
        report.Refresh(paths);
        Assert.That(report.Repositories.ContainsKey(tempRepo.Path), Is.False);
    }

    [Test]
    public void Refresh_WithFetchRemotes_DoesNotThrow()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { fixture.Path };
        report.Refresh(paths, fetchRemotes: true);

        Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
    }

    [Test]
    public void Refresh_WithDeepRefresh_DoesNotThrow()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { fixture.Path };
        report.Refresh(paths, deepRefresh: true);

        Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
    }

    [Test]
    public void Refresh_WithAllBranches_PopulatesBranches()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { fixture.Path };
        report.Refresh(paths, allBranches: true);

        Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
    }

    [Test]
    public void Refresh_WithComputeLocalCommitCount_False_SkipsCommitCounting()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { fixture.Path };
        report.Refresh(paths, computeLocalCommitCount: false);

        var repo = report.Repositories[fixture.Path];
        Assert.That(repo.LocalCommitCount, Is.EqualTo(0));
        Assert.That(repo.LocalOnlyCommits, Is.False);
    }

    [Test]
    public async Task GenerateReport_Options_ComputeLocalCommitCount_False_SkipsCounting()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var options = new GitWizardReportOptions { ComputeLocalCommitCount = false };
        var report = await GitWizardReport.GenerateReportAsync(configuration, new List<string> { fixture.Path }, options: options);

        var repo = report.Repositories[fixture.Path];
        Assert.That(repo.LocalCommitCount, Is.EqualTo(0));
        Assert.That(repo.LocalOnlyCommits, Is.False);
    }

    [Test]
    public async Task GetRepositoryPaths_WithEmptySearchPaths_DoesNotThrow()
    {
        var report = new GitWizardReport();
        var paths = new SortedSet<string>();

        // noMft: true skips MFT discovery so the test pops no UAC on Windows; with empty search
        // paths the recursive fallback is a no-op, so the assertion is unchanged.
        await report.GetRepositoryPathsAsync(paths, noMft: true);

        Assert.That(paths, Is.Empty);
    }

    [Test]
    public void Save_CreatesReportFile()
    {
        var report = new GitWizardReport();
        using var files = new TempDirectory();
        var reportPath = Path.Combine(files.Path, "report.json");

        report.Save(reportPath);
        Assert.That(File.Exists(reportPath), Is.True);
    }

    [Test]
    public async Task SaveAsync_CreatesReportFile()
    {
        var report = new GitWizardReport();
        using var files = new TempDirectory();
        var reportPath = Path.Combine(files.Path, "report.json");

        await report.SaveAsync(reportPath);
        Assert.That(File.Exists(reportPath), Is.True);
    }

    [Test]
    public void Save_SerializesRepositories()
    {
        var report = new GitWizardReport();
        report.Repositories["/test/repo"] = new GitWizardRepository("/test/repo");

        using var files = new TempDirectory();
        var reportPath = Path.Combine(files.Path, "report.json");

        report.Save(reportPath);
        var json = File.ReadAllText(reportPath);
        Assert.That(json, Does.Contain("\"/test/repo\""));
    }

    [Test]
    public void GetRepositoryPaths_Enumerable_ReturnsKeys()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/1"] = new GitWizardRepository("/repo/1");
        report.Repositories["/repo/2"] = new GitWizardRepository("/repo/2");

        var paths = report.GetRepositoryPaths().ToList();

        Assert.That(paths, Has.Count.EqualTo(2));
        Assert.That(paths, Does.Contain("/repo/1"));
        Assert.That(paths, Does.Contain("/repo/2"));
    }

    [Test]
    public void DeletedPaths_InitializedAsEmptySet()
    {
        var report = new GitWizardReport();
        Assert.That(report.DeletedPaths, Is.Not.Null);
        Assert.That(report.DeletedPaths, Is.Empty);
    }

    [Test]
    public void GetRepositoryPaths_ExcludesNonRepositoryPaths()
    {
        // Verifies the CLI's implicit cache-pruning contract: GetRepositoryPaths() only
        // returns keys from the Repositories dictionary (which excludes DeletedPaths and
        // NonRepositoryPaths that were pruned during Refresh).  The CLI calls this when
        // saving repositories.txt back to disk, so stale entries never get persisted.
        GitWizardLog.SilentMode = true;
        using var validRepo = TempRepoFixture.CreateWithInitialCommit();
        using var nonRepo = new TempDirectory("not-a-repo-");
        var nonRepoPath = nonRepo.Path;

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { validRepo.Path, nonRepoPath };
        report.Refresh(paths);

        // NonRepositoryPaths tracks the stale entry and Repositories excludes it.
        Assert.That(report.NonRepositoryPaths, Is.Not.Empty);
        Assert.That(report.Repositories.ContainsKey(nonRepoPath), Is.False);

        // The CLI's implicit pruning contract: GetRepositoryPaths() only yields
        // healthy repo paths, so the stale entry is excluded from the list that
        // gets saved back to repositories.txt.
        var savedPaths = report.GetRepositoryPaths().ToList();
        Assert.That(savedPaths, Does.Not.Contain(nonRepoPath));
        Assert.That(savedPaths, Does.Contain(validRepo.Path));
    }

    [Test]
    public void GetRepositoryPaths_ExcludesDeletedPaths()
    {
        // Verifies that deleted (transient) paths are excluded from GetRepositoryPaths
        // just like non-repository paths, so they don't get written back to
        // repositories.txt either.  Deleted paths are transient (unmounted drive) but
        // the CLI doesn't save them to cache because they were removed from Repositories
        // before GetRepositoryPaths was called.
        GitWizardLog.SilentMode = true;
        using var tempRepo = TempRepoFixture.CreateWithInitialCommit();
        var deletedPath = tempRepo.Path;

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { deletedPath };
        report.Refresh(paths);

        Assert.That(report.Repositories.ContainsKey(deletedPath), Is.True,
            "Precondition: intact repo should be in Repositories.");

        // Delete the repo directory to simulate transient unmount.
        tempRepo.DeleteNow();

        // Refresh again with the deleted path.
        report.Repositories.Clear();
        report.Refresh(paths);

        // Deleted path is in DeletedPaths and NOT in Repositories.
        Assert.That(report.DeletedPaths.Contains(deletedPath), Is.True);
        Assert.That(report.Repositories.ContainsKey(deletedPath), Is.False);

        // GetRepositoryPaths yields only healthy repos (none in this case).
        var savedPaths = report.GetRepositoryPaths().ToList();
        Assert.That(savedPaths, Does.Not.Contain(deletedPath));
    }

    [Test]
    public void GetRepositoryPaths_MixedHealthyAndStale_YieldsOnlyHealthy()
    {
        // End-to-end: valid repo + non-repo directory + deleted directory => only the valid
        // repo appears in GetRepositoryPaths, matching the CLI's save-back-to-cache behavior.
        GitWizardLog.SilentMode = true;
        using var validRepo = TempRepoFixture.CreateWithInitialCommit();
        using var nonRepo = new TempDirectory("not-a-repo-");
        var nonRepoPath = nonRepo.Path;
        using var deleted = new TempDirectory("deleted-", create: false);
        var deletedPath = deleted.Path;

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { validRepo.Path, nonRepoPath, deletedPath };
        report.Refresh(paths);

        Assert.That(report.Repositories.ContainsKey(validRepo.Path), Is.True);
        Assert.That(report.NonRepositoryPaths.Contains(nonRepoPath), Is.True);
        Assert.That(report.DeletedPaths.Contains(deletedPath), Is.True);

        // GetRepositoryPaths yields only healthy repos.
        var savedPaths = report.GetRepositoryPaths().ToList();
        Assert.That(savedPaths, Has.Count.EqualTo(1));
        Assert.That(savedPaths[0], Is.EqualTo(validRepo.Path));
    }
}
