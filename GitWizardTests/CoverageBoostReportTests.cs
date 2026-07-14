using GitWizard;

namespace GitWizardTests;

/// <summary>
/// Coverage-boost tests for <see cref="GitWizardReport"/>: GenerateReport edge cases,
/// Refresh method paths, save/load round-trips, GetRepositoryPaths enumerator,
/// cached report paths, and async variants.
/// </summary>
public class CoverageBoostReportTests
{
    string? _tempRoot;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _tempRoot = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();
    }

    [TearDown]
    public void TearDown()
    {
        TestUtilities.ResetStaticCaches();
        TestUtilities.ClearLocalFilesRedirect(_tempRoot);
    }

    [Test]
    public void GenerateReport_WithNoRepos_CreatesEmptyReport()
    {
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = GitWizardReport.GenerateReport(config, new List<string>());

        Assert.Multiple(() =>
        {
            Assert.That(report.Repositories, Is.Empty);
            Assert.That(report.BranchScope, Is.EqualTo("actionable"));
            Assert.That(report.SchemaVersion, Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
        });
    }

    [Test]
    public void GenerateReport_AllBranches_SetsBranchScopeToAll()
    {
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var options = new GitWizardReportOptions { AllBranches = true };
        var report = GitWizardReport.GenerateReport(config, new List<string>(), options: options);

        Assert.That(report.BranchScope, Is.EqualTo("all"));
    }

    [Test]
    public void GenerateReport_WithRealRepo_PopulatesRepository()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = GitWizardReport.GenerateReport(config, new List<string> { fixture.Path });

        Assert.That(report.Repositories, Has.Count.EqualTo(1));
        Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
    }

    [Test]
    public void GenerateReport_NullPaths_TriesDiscovery()
    {
        var config = new GitWizardConfiguration
        {
            SearchPaths = new SortedSet<string>(), // empty search paths = no discovery
            IgnoredPaths = new SortedSet<string>()
        };
        var options = new GitWizardReportOptions { NoMft = true };
        var report = GitWizardReport.GenerateReport(config, options: options);

        Assert.That(report, Is.Not.Null);
        Assert.That(report.Repositories, Is.Empty);
    }

    [Test]
    public void Refresh_WithUpdateHandler_CallsStartProgressAndUpdateProgress()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(config);
        var handler = new ProgressTrackingHandler();

        report.Refresh(new SortedSet<string> { fixture.Path }, handler);

        Assert.Multiple(() =>
        {
            Assert.That(handler.StartProgressCalled, Is.True);
            Assert.That(handler.UpdateProgressCalled, Is.True);
            Assert.That(handler.RepositoriesCreated, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Refresh_ThrowingStartProgressHandler_DoesNotCrash()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(config);
        var handler = new ThrowingStartProgressHandler();

        Assert.DoesNotThrow(() => report.Refresh(new SortedSet<string> { fixture.Path }, handler));
    }

    [Test]
    public void Refresh_ThrowingOnRepositoryCreatedHandler_DoesNotCrash()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(config);
        var handler = new ThrowingOnCreatedHandler();

        Assert.DoesNotThrow(() => report.Refresh(new SortedSet<string> { fixture.Path }, handler));
    }

    [Test]
    public void Save_ThenGetCachedReport_RoundTrips()
    {
        var report = new GitWizardReport();
        report.Repositories["/test/repo"] = new GitWizardRepository("/test/repo");

        var path = GitWizardReport.GetCachedReportPath();
        report.Save(path);

        TestUtilities.ResetStaticCaches();
        var loaded = GitWizardReport.GetCachedReport();

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void GetCachedReport_ReturnsNull_WhenNoFile()
    {
        var result = GitWizardReport.GetCachedReport();
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetCachedReport_ReturnsCachedInstance_OnSecondCall()
    {
        var report = new GitWizardReport();
        report.Save(GitWizardReport.GetCachedReportPath());

        var first = GitWizardReport.GetCachedReport();
        var second = GitWizardReport.GetCachedReport();

        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.SameAs(first), "Second call should return the cached instance.");
    }

    [Test]
    public void GetCachedReport_InvalidJson_ReturnsNull()
    {
        GitWizardApi.EnsureLocalFolderExists();
        File.WriteAllText(GitWizardReport.GetCachedReportPath(), "not json {{{");

        var result = GitWizardReport.GetCachedReport();
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetCachedReportAsync_InvalidJson_ReturnsNull()
    {
        GitWizardApi.EnsureLocalFolderExists();
        await File.WriteAllTextAsync(GitWizardReport.GetCachedReportPath(), "not json {{{");

        var result = await GitWizardReport.GetCachedReportAsync();
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetCachedReportAsync_CachedInstance_ReturnsFromMemory()
    {
        var report = new GitWizardReport();
        await report.SaveAsync(GitWizardReport.GetCachedReportPath());

        var first = await GitWizardReport.GetCachedReportAsync();
        var second = await GitWizardReport.GetCachedReportAsync();

        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void Save_CreatesDirectoryIfNeeded()
    {
        var report = new GitWizardReport();
        var nested = Path.Combine(_tempRoot!, "deep", "nested", "report.json");
        report.Save(nested);
        Assert.That(File.Exists(nested), Is.True);
    }

    [Test]
    public void Save_StampsSchemaVersion()
    {
        var report = new GitWizardReport();
        report.SchemaVersion = "old";
        var path = Path.Combine(_tempRoot!, "version-test.json");
        report.Save(path);

        var json = File.ReadAllText(path);
        Assert.That(json, Does.Contain(GitWizardReport.CurrentSchemaVersion));
    }

    [Test]
    public async Task SaveAsync_StampsSchemaVersion()
    {
        var report = new GitWizardReport();
        report.SchemaVersion = "old";
        var path = Path.Combine(_tempRoot!, "version-async-test.json");
        await report.SaveAsync(path);

        var json = await File.ReadAllTextAsync(path);
        Assert.That(json, Does.Contain(GitWizardReport.CurrentSchemaVersion));
    }

    [Test]
    public void Constructor_WithConfiguration_CopiesSearchAndIgnoredPaths()
    {
        var config = new GitWizardConfiguration
        {
            SearchPaths = { "/a", "/b" },
            IgnoredPaths = { "/c" }
        };

        var report = new GitWizardReport(config);

        Assert.Multiple(() =>
        {
            Assert.That(report.SearchPaths, Has.Count.EqualTo(2));
            Assert.That(report.IgnoredPaths, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void GetRepositoryPaths_Enumerator_ReturnsAllKeys()
    {
        var report = new GitWizardReport();
        report.Repositories["/a"] = new GitWizardRepository("/a");
        report.Repositories["/b"] = new GitWizardRepository("/b");
        report.Repositories["/c"] = new GitWizardRepository("/c");

        var paths = report.GetRepositoryPaths().ToList();
        Assert.That(paths, Has.Count.EqualTo(3));
    }

    [Test]
    public void RefreshSingleRepository_SetsRefreshTimeSeconds()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(config);

        report.Refresh(new SortedSet<string> { fixture.Path });

        var repo = report.Repositories[fixture.Path];
        Assert.That(repo.RefreshTimeSeconds, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Refresh_EmptyPaths_DoesNotThrow()
    {
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(config);
        Assert.DoesNotThrow(() => report.Refresh(new SortedSet<string>()));
    }

    [Test]
    public void NonRepositoryPaths_InitializedAsEmptySet()
    {
        var report = new GitWizardReport();
        Assert.That(report.NonRepositoryPaths, Is.Not.Null);
        Assert.That(report.NonRepositoryPaths, Is.Empty);
    }

    [Test]
    public void Refresh_MultipleNonRepoPaths_AllTracked()
    {
        var dir1 = Path.Combine(Path.GetTempPath(), "gw-nonrepo1-" + Guid.NewGuid().ToString("N"));
        var dir2 = Path.Combine(Path.GetTempPath(), "gw-nonrepo2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        try
        {
            var config = GitWizardConfiguration.CreateDefaultConfiguration();
            var report = new GitWizardReport(config);
            report.Refresh(new SortedSet<string> { dir1, dir2 });

            Assert.That(report.NonRepositoryPaths, Has.Count.EqualTo(2));
        }
        finally
        {
            Directory.Delete(dir1, recursive: true);
            Directory.Delete(dir2, recursive: true);
        }
    }

    sealed class ProgressTrackingHandler : IUpdateHandler
    {
        public bool StartProgressCalled { get; private set; }
        public bool UpdateProgressCalled { get; private set; }
        public int RepositoriesCreated { get; private set; }

        public void StartProgress(string description, int total) => StartProgressCalled = true;
        public void UpdateProgress(int count) => UpdateProgressCalled = true;
        public void SendUpdateMessage(string? message) { }
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) => RepositoriesCreated++;
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    }

    sealed class ThrowingStartProgressHandler : IUpdateHandler
    {
        public void StartProgress(string description, int total) =>
            throw new InvalidOperationException("StartProgress failure");
        public void UpdateProgress(int count) =>
            throw new InvalidOperationException("UpdateProgress failure");
        public void SendUpdateMessage(string? message) { }
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    }

    sealed class ThrowingOnCreatedHandler : IUpdateHandler
    {
        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) { }
        public void SendUpdateMessage(string? message) { }
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) =>
            throw new InvalidOperationException("OnRepositoryCreated failure");
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    }
}
