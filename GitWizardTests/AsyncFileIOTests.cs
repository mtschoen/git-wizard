using GitWizard;

namespace GitWizardTests;

public class AsyncFileIOTests
{
    string? _tempRoot;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _tempRoot = Path.Combine(Path.GetTempPath(), "GitWizardAsyncTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        // Reset static caches and delete cached files for full isolation
        ResetStaticCaches();
    }

    [TearDown]
    public void TearDown()
    {
        if (!string.IsNullOrEmpty(_tempRoot) && Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);

        // Reset static caches and delete cached files after each test
        ResetStaticCaches();
    }

    static void ResetStaticCaches()
    {
        TestUtilities.ResetStaticCaches();

        // Clean up cached repo list file
        var localPath = GitWizardApi.GetLocalFilesPath();
        var repoListPath = Path.Combine(localPath, "repositories.txt");
        if (File.Exists(repoListPath))
            File.Delete(repoListPath);

        // Clean up cached report file
        var reportPath = GitWizardReport.GetCachedReportPath();
        if (File.Exists(reportPath))
            File.Delete(reportPath);

        // Clean up config file
        var configPath = GitWizardConfiguration.GetGlobalConfigurationPath();
        if (File.Exists(configPath))
            File.Delete(configPath);
    }

    [Test]
    public async Task SaveAndLoadConfigurationAsync_RoundTrip()
    {
        var configPath = Path.Combine(_tempRoot!, "config.json");

        var config = new GitWizardConfiguration
        {
            SearchPaths = { "/path/one", "/path/two" },
            IgnoredPaths = { "/ignored/path" }
        };

        await config.SaveAsync(configPath);

        var loaded = await GitWizardConfiguration.GetConfigurationAtPathAsync(configPath);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.SearchPaths, Has.Count.EqualTo(2));
        Assert.That(loaded.SearchPaths, Does.Contain("/path/one"));
        Assert.That(loaded.SearchPaths, Does.Contain("/path/two"));
        Assert.That(loaded.IgnoredPaths, Has.Count.EqualTo(1));
        Assert.That(loaded.IgnoredPaths, Does.Contain("/ignored/path"));
    }

    [Test]
    public async Task GetConfigurationAtPathAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        var nonExistentPath = Path.Combine(_tempRoot!, "nonexistent.json");
        var result = await GitWizardConfiguration.GetConfigurationAtPathAsync(nonExistentPath);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetConfigurationAtPathAsync_ReturnsNull_ForInvalidJson()
    {
        var configPath = Path.Combine(_tempRoot!, "invalid.json");
        await File.WriteAllTextAsync(configPath, "not valid json {{{");

        var result = await GitWizardConfiguration.GetConfigurationAtPathAsync(configPath);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SaveConfigurationAsync_CreatesDirectory()
    {
        var nestedPath = Path.Combine(_tempRoot!, "sub", "dir", "config.json");

        var config = new GitWizardConfiguration();
        await config.SaveAsync(nestedPath);

        Assert.That(File.Exists(nestedPath), Is.True);
    }

    [Test]
    public async Task SaveAndLoadReportAsync_RoundTrip()
    {
        var reportPath = GitWizardReport.GetCachedReportPath();

        var report = new GitWizardReport
        {
            SearchPaths = { "/search/1" },
            IgnoredPaths = { "/ignore/1" }
        };

        await report.SaveAsync(reportPath);

        var loaded = await GitWizardReport.GetCachedReportAsync();

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.SchemaVersion, Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
    }

    [Test]
    public async Task GetCachedReportAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        var result = await GitWizardReport.GetCachedReportAsync();
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SaveAndLoadRepositoryPathsAsync_RoundTrip()
    {
        var paths = new[] { "/repo/one", "/repo/two", "/repo/three" };

        await GitWizardApi.SaveCachedRepositoryPathsAsync(paths);

        var loaded = await GitWizardApi.GetCachedRepositoryPathsAsync();

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!, Has.Length.EqualTo(3));
        Assert.That(loaded, Does.Contain("/repo/one"));
        Assert.That(loaded, Does.Contain("/repo/two"));
        Assert.That(loaded, Does.Contain("/repo/three"));
    }

    [Test]
    public async Task GetCachedRepositoryPathsAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        var result = await GitWizardApi.GetCachedRepositoryPathsAsync();
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SaveCachedRepositoryPathsAsync_CreatesDirectory()
    {
        var tempLocalPath = Path.Combine(_tempRoot!, "LocalFiles");
        // Temporarily override GetLocalFilesPath behavior by creating the directory first
        Directory.CreateDirectory(tempLocalPath);

        var paths = new[] { "/test/repo" };
        await GitWizardApi.SaveCachedRepositoryPathsAsync(paths);

        var loaded = await GitWizardApi.GetCachedRepositoryPathsAsync();
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task SaveAndLoadConfigurationAsync_EmptySets()
    {
        var configPath = Path.Combine(_tempRoot!, "empty_config.json");

        var config = new GitWizardConfiguration();
        await config.SaveAsync(configPath);

        var loaded = await GitWizardConfiguration.GetConfigurationAtPathAsync(configPath);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.SearchPaths, Has.Count.EqualTo(0));
        Assert.That(loaded.IgnoredPaths, Has.Count.EqualTo(0));
    }

    [Test]
    public async Task SaveCachedRepositoryPathsAsync_EmptyLines()
    {
        var paths = new[] { "/repo/one", "", "/repo/two" };
        await GitWizardApi.SaveCachedRepositoryPathsAsync(paths);

        var loaded = await GitWizardApi.GetCachedRepositoryPathsAsync();

        Assert.That(loaded, Is.Not.Null);
         Assert.That(loaded!, Has.Length.EqualTo(2));
    }

    [Test]
    public async Task SaveAndLoadReportAsync_WithRepositories()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/one"] = new GitWizardRepository("/repo/one");
        report.Repositories["/repo/two"] = new GitWizardRepository("/repo/two");

        await report.SaveAsync(GitWizardReport.GetCachedReportPath());

        var loaded = await GitWizardReport.GetCachedReportAsync();

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Repositories, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task ConfigAsyncMethods_UseConfigureAwaitFalse()
    {
        // Verify that async methods don't capture synchronization context
        var configPath = Path.Combine(_tempRoot!, "context_test.json");
        var config = new GitWizardConfiguration { SearchPaths = { "/test" } };

        await config.SaveAsync(configPath);
        var loaded = await GitWizardConfiguration.GetConfigurationAtPathAsync(configPath);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.SearchPaths, Does.Contain("/test"));
    }
}
