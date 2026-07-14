using GitWizard;

namespace GitWizardTests;

public class GitWizardReportAdditionalTests
{
    string? _tempRoot;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        // Redirect the data dir to temp: the GetCachedReport* / InvalidJson tests write to
        // GetCachedReportPath() (the real ~/.GitWizard/report.json without this). _tempRoot
        // doubles as the scratch dir for the ad-hoc Save_* paths below.
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
    public void DefaultCtor_HasEmptySearchPaths()
    {
        var report = new GitWizardReport();
        Assert.That(report.SearchPaths, Is.Not.Null);
        Assert.That(report.SearchPaths, Is.Empty);
    }

    [Test]
    public void DefaultCtor_HasEmptyIgnoredPaths()
    {
        var report = new GitWizardReport();
        Assert.That(report.IgnoredPaths, Is.Not.Null);
        Assert.That(report.IgnoredPaths, Is.Empty);
    }

    [Test]
    public void DefaultCtor_HasEmptyRepositories()
    {
        var report = new GitWizardReport();
        Assert.That(report.Repositories, Is.Not.Null);
        Assert.That(report.Repositories, Is.Empty);
    }

    [Test]
    public void DefaultCtor_HasEmptyDeletedPaths()
    {
        var report = new GitWizardReport();
        Assert.That(report.DeletedPaths, Is.Not.Null);
        Assert.That(report.DeletedPaths, Is.Empty);
    }

    [Test]
    public void Constructor_FromConfiguration_CopiesSearchPaths()
    {
        var config = new GitWizardConfiguration
        {
            SearchPaths = { "/search1", "/search2" }
        };
        var report = new GitWizardReport(config);

        Assert.That(report.SearchPaths, Does.Contain("/search1"));
        Assert.That(report.SearchPaths, Does.Contain("/search2"));
    }

    [Test]
    public void Constructor_FromConfiguration_CopiesIgnoredPaths()
    {
        var config = new GitWizardConfiguration
        {
            IgnoredPaths = { "/ignore1" }
        };
        var report = new GitWizardReport(config);

        Assert.That(report.IgnoredPaths, Does.Contain("/ignore1"));
    }

    [Test]
    public void GetCachedReportPath_ReturnsCorrectPath()
    {
        var localPath = GitWizardApi.GetLocalFilesPath();
        var cachedPath = GitWizardReport.GetCachedReportPath();
        Assert.That(cachedPath, Does.EndWith(Path.Combine(localPath, "report.json")));
    }

    [Test]
    public async Task GetRepositoryPaths_EmptyConfiguration_FindsNothing()
    {
        var report = new GitWizardReport();
        var paths = new SortedSet<string>();
        // noMft: true skips MFT discovery (no UAC on Windows); empty config finds nothing either way.
        await report.GetRepositoryPathsAsync(paths, noMft: true);
        Assert.That(paths, Is.Empty);
    }

    [Test]
    public void Save_CreatesDirectoryIfNeeded()
    {
        var report = new GitWizardReport();
        var nestedPath = Path.Combine(_tempRoot!, "sub", "dir", "report.json");

        report.Save(nestedPath);

        Assert.That(File.Exists(nestedPath), Is.True);
    }

    [Test]
    public void Save_WritesValidJson()
    {
        var report = new GitWizardReport
        {
            SearchPaths = { "/test" }
        };

        var jsonPath = Path.Combine(_tempRoot!, "test_report.json");
        report.Save(jsonPath);

        var json = File.ReadAllText(jsonPath);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<GitWizardReport>(json);
        Assert.That(deserialized!.SchemaVersion, Is.EqualTo("2.1"));
    }

    [Test]
    public async Task SaveAsync_CreatesDirectoryIfNeeded()
    {
        var report = new GitWizardReport();
        var nestedPath = Path.Combine(_tempRoot!, "async", "dir", "report.json");

        await report.SaveAsync(nestedPath);

        Assert.That(File.Exists(nestedPath), Is.True);
    }

    [Test]
    public async Task GetCachedReportAsync_CachesReport()
    {
        // SaveAsync always stamps CurrentSchemaVersion, overriding this initializer - see below.
        var report = new GitWizardReport { SchemaVersion = "2.0" };
        var jsonPath = GitWizardReport.GetCachedReportPath();

        await report.SaveAsync(jsonPath);

        var loaded = await GitWizardReport.GetCachedReportAsync();
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.SchemaVersion, Is.EqualTo("2.1"));
    }

    [Test]
    public void GetCachedReport_CachesReport()
    {
        // Save always stamps CurrentSchemaVersion, overriding this initializer - see below.
        var report = new GitWizardReport { SchemaVersion = "2.0" };
        var jsonPath = GitWizardReport.GetCachedReportPath();

        report.Save(jsonPath);

        var loaded = GitWizardReport.GetCachedReport();
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.SchemaVersion, Is.EqualTo("2.1"));
    }

    [Test]
    public void GetCachedReport_ReturnsCachedInstance()
    {
        var report = new GitWizardReport();
        var jsonPath = GitWizardReport.GetCachedReportPath();
        report.Save(jsonPath);

        var first = GitWizardReport.GetCachedReport();
        var second = GitWizardReport.GetCachedReport();

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void Save_WithRepositories_SerializesAll()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/one"] = new GitWizardRepository("/repo/one");
        report.Repositories["/repo/two"] = new GitWizardRepository("/repo/two");

        var jsonPath = Path.Combine(_tempRoot!, "repos_test.json");
        report.Save(jsonPath);

        var json = File.ReadAllText(jsonPath);
        Assert.That(json, Does.Contain("\"/repo/one\""));
        Assert.That(json, Does.Contain("\"/repo/two\""));
    }

    [Test]
    public async Task GetRepositoryPaths_WithNonExistentSearchPath_DoesNotThrow()
    {
        var report = new GitWizardReport();
        report.SearchPaths.Add("/nonexistent/search/path");

        var paths = new SortedSet<string>();
        // noMft: true skips MFT discovery (no UAC on Windows); the nonexistent path yields nothing
        // from the recursive fallback, so the assertion is unchanged.
        await report.GetRepositoryPathsAsync(paths, noMft: true);

        Assert.That(paths, Is.Empty);
    }

    [Test]
    public void DeletedPaths_Populated_OnRefresh()
    {
        using var tempRepo = TempRepoFixture.CreateWithInitialCommit();

        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(config);

        var paths = new SortedSet<string> { tempRepo.Path };
        report.Refresh(paths);
        Assert.That(report.Repositories.ContainsKey(tempRepo.Path), Is.True);
    }

    [Test]
    public void RepositoryKeys_Enumerable()
    {
        var report = new GitWizardReport();
        report.Repositories["/a"] = new GitWizardRepository("/a");
        report.Repositories["/b"] = new GitWizardRepository("/b");

        var keys = report.GetRepositoryPaths().ToList();

        Assert.That(keys, Has.Count.EqualTo(2));
        Assert.That(keys, Does.Contain("/a"));
        Assert.That(keys, Does.Contain("/b"));
    }

    [Test]
    public async Task GetCachedReportAsync_ReturnsCached_WhenAlreadyLoaded()
    {
        var report = new GitWizardReport();
        var jsonPath = GitWizardReport.GetCachedReportPath();
        report.Save(jsonPath);

        var first = await GitWizardReport.GetCachedReportAsync();
        var second = await GitWizardReport.GetCachedReportAsync();

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void GetCachedReport_InvalidJson_ReturnsNull()
    {
        var jsonPath = GitWizardReport.GetCachedReportPath();
        File.WriteAllText(jsonPath, "not valid json");

        var report = GitWizardReport.GetCachedReport();

        Assert.That(report, Is.Null);
    }

    [Test]
    public async Task GetCachedReportAsync_InvalidJson_ReturnsNull()
    {
        var jsonPath = GitWizardReport.GetCachedReportPath();
        await File.WriteAllTextAsync(jsonPath, "not valid json");

        TestUtilities.ResetStaticCaches();
        var report = await GitWizardReport.GetCachedReportAsync();

        Assert.That(report, Is.Null);
    }

    [Test]
    public void BranchScope_DefaultsToNull()
    {
        var report = new GitWizardReport();
        Assert.That(report.BranchScope, Is.Null);
    }

    [Test]
    public void BranchScope_SetsToActionable()
    {
        var report = new GitWizardReport();
        report.BranchScope = "actionable";
        Assert.That(report.BranchScope, Is.EqualTo("actionable"));
    }

    [Test]
    public void BranchScope_SetsToAll()
    {
        var report = new GitWizardReport();
        report.BranchScope = "all";
        Assert.That(report.BranchScope, Is.EqualTo("all"));
    }

    [Test]
    public void Repositories_IsSortedDictionary()
    {
        var report = new GitWizardReport();
        Assert.That(report.Repositories, Is.InstanceOf<SortedDictionary<string, GitWizardRepository>>());
    }

    [Test]
    public void SearchPaths_IsSortedSet()
    {
        var report = new GitWizardReport();
        Assert.That(report.SearchPaths, Is.InstanceOf<SortedSet<string>>());
    }

    [Test]
    public void IgnoredPaths_IsSortedSet()
    {
        var report = new GitWizardReport();
        Assert.That(report.IgnoredPaths, Is.InstanceOf<SortedSet<string>>());
    }

    [Test]
    public void DeletedPaths_IsHashSet()
    {
        var report = new GitWizardReport();
        Assert.That(report.DeletedPaths, Is.InstanceOf<HashSet<string>>());
    }
}
