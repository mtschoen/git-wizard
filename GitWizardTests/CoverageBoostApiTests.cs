using GitWizard;

namespace GitWizardTests;

/// <summary>
/// Coverage-boost tests for <see cref="GitWizardApi"/>: GetLocalFilesSize with content,
/// ClearCache with populated files, DeleteAllLocalFiles with existing data,
/// cached path save/load sync variants, and path helper edge cases.
/// </summary>
public class CoverageBoostApiTests
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
    public void GetLocalFilesSize_WithContent_ReturnsPositive()
    {
        GitWizardApi.EnsureLocalFolderExists();
        File.WriteAllText(Path.Combine(GitWizardApi.GetLocalFilesPath(), "dummy.txt"), "some content here");

        var size = GitWizardApi.GetLocalFilesSize();
        Assert.That(size, Is.GreaterThan(0));
    }

    [Test]
    public void GetLocalFilesSize_WithSubdirectories_IncludesAll()
    {
        GitWizardApi.EnsureLocalFolderExists();
        var subDir = Path.Combine(GitWizardApi.GetLocalFilesPath(), "subdir");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "nested.txt"), "nested content");

        var size = GitWizardApi.GetLocalFilesSize();
        Assert.That(size, Is.GreaterThan(0));
    }

    [Test]
    public void ClearCache_WithPopulatedFiles_DeletesBoth()
    {
        GitWizardApi.EnsureLocalFolderExists();
        var repoListPath = GitWizardApi.GetCachedRepositoryListPath();
        var reportPath = GitWizardReport.GetCachedReportPath();
        File.WriteAllText(repoListPath, "path1\npath2\npath3");
        File.WriteAllText(reportPath, "{}");

        GitWizardApi.ClearCache();

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(repoListPath), Is.False);
            Assert.That(File.Exists(reportPath), Is.False);
        });
    }

    [Test]
    public void ClearCache_OnlyRepoList_DeletesOnlyRepoList()
    {
        GitWizardApi.EnsureLocalFolderExists();
        var repoListPath = GitWizardApi.GetCachedRepositoryListPath();
        File.WriteAllText(repoListPath, "path1");

        GitWizardApi.ClearCache();

        Assert.That(File.Exists(repoListPath), Is.False);
    }

    [Test]
    public void DeleteAllLocalFiles_WithContent_RemovesDirectory()
    {
        GitWizardApi.EnsureLocalFolderExists();
        File.WriteAllText(Path.Combine(GitWizardApi.GetLocalFilesPath(), "data.txt"), "data");

        GitWizardApi.DeleteAllLocalFiles();

        Assert.That(Directory.Exists(GitWizardApi.GetLocalFilesPath()), Is.False);
    }

    [Test]
    public void DeleteAllLocalFiles_NoDirectory_DoesNotThrow()
    {
        // Ensure the directory does not exist
        var path = GitWizardApi.GetLocalFilesPath();
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);

        Assert.DoesNotThrow(() => GitWizardApi.DeleteAllLocalFiles());
    }

    [Test]
    public void SaveCachedRepositoryPaths_ThenLoad_Sync_RoundTrips()
    {
        var paths = new[] { "/repo/x", "/repo/y", "/repo/z" };
        GitWizardApi.SaveCachedRepositoryPaths(paths);

        var loaded = GitWizardApi.GetCachedRepositoryPaths();
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!, Has.Length.EqualTo(3));
        Assert.That(loaded, Does.Contain("/repo/x"));
    }

    [Test]
    public void GetCachedRepositoryPaths_WhitespaceOnly_ReturnsNull()
    {
        GitWizardApi.EnsureLocalFolderExists();
        File.WriteAllText(GitWizardApi.GetCachedRepositoryListPath(), "   \n  \n  ");

        var result = GitWizardApi.GetCachedRepositoryPaths();
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetCachedRepositoryPathsAsync_WhitespaceOnly_ReturnsNull()
    {
        GitWizardApi.EnsureLocalFolderExists();
        await File.WriteAllTextAsync(GitWizardApi.GetCachedRepositoryListPath(), "   \n  \n  ");

        var result = await GitWizardApi.GetCachedRepositoryPathsAsync();
        Assert.That(result, Is.Null);
    }

    [Test]
    public void EnsureLocalFolderExists_Idempotent()
    {
        GitWizardApi.EnsureLocalFolderExists();
        Assert.That(Directory.Exists(GitWizardApi.GetLocalFilesPath()), Is.True);
        GitWizardApi.EnsureLocalFolderExists(); // second call should not throw
        Assert.That(Directory.Exists(GitWizardApi.GetLocalFilesPath()), Is.True);
    }

    [Test]
    public void GetLogFolderPath_ContainsLogs()
    {
        var logPath = GitWizardApi.GetLogFolderPath();
        Assert.That(logPath, Does.Contain("Logs"));
    }

    [Test]
    public void ExpandSearchPath_TildePrefix_ExpandsToUserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // ~/Documents should expand only if ~/Documents exists
        if (Directory.Exists(Path.Combine(profile, "Documents")))
        {
            var result = GitWizardApi.ExpandSearchPath("~/Documents");
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("Documents").IgnoreCase);
        }
        else
        {
            var result = GitWizardApi.ExpandSearchPath("~/Documents");
            Assert.That(result, Is.Null);
        }
    }

    [Test]
    public void PrettyPrintPath_BackslashTilde_Expands()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = GitWizardApi.PrettyPrintPath(@"~\Documents");
        Assert.That(result, Is.EqualTo(Path.Combine(profile, "Documents")));
    }

    [Test]
    public async Task SaveCachedRepositoryPathsAsync_ThenLoadSync_Works()
    {
        await GitWizardApi.SaveCachedRepositoryPathsAsync(new[] { "/async/to/sync" });
        var loaded = GitWizardApi.GetCachedRepositoryPaths();
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!, Does.Contain("/async/to/sync"));
    }

    [Test]
    public void SaveCachedRepositoryPaths_ThenLoadAsync_Works()
    {
        GitWizardApi.SaveCachedRepositoryPaths(new[] { "/sync/to/async" });
        var loaded = GitWizardApi.GetCachedRepositoryPathsAsync().GetAwaiter().GetResult();
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!, Does.Contain("/sync/to/async"));
    }
}
