using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

[TestFixture, NonParallelizable]
public class GitWizardApiLocalFilesTests
{
    LocalFilesSandbox? _sandbox;
    bool _previousSilent;
    bool _previousVerbose;

    [SetUp]
    public void SetUp()
    {
        _previousSilent = GitWizardLog.SilentMode;
        _previousVerbose = GitWizardLog.VerboseMode;
        GitWizardLog.SilentMode = true;
        _sandbox = new LocalFilesSandbox();
    }

    [TearDown]
    public void TearDown()
    {
        _sandbox?.Dispose();
        _sandbox = null;
        GitWizardLog.SilentMode = _previousSilent;
        GitWizardLog.VerboseMode = _previousVerbose;
    }

    static void SaveSync(IEnumerable<string> paths) => GitWizardApi.SaveCachedRepositoryPaths(paths);
    static string[]? LoadSync() => GitWizardApi.GetCachedRepositoryPaths();

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task CachedRepositoryPaths_RoundTrip(bool saveAsync, bool loadAsync)
    {
        string[] paths = ["/repo/one", "", " /repo/two "];
        if (saveAsync)
        {
            await GitWizardApi.SaveCachedRepositoryPathsAsync(paths);
        }
        else
        {
            SaveSync(paths);
        }

        var actual = loadAsync
            ? await GitWizardApi.GetCachedRepositoryPathsAsync()
            : LoadSync();
        Assert.That(File.Exists(GitWizardApi.GetCachedRepositoryListPath()), Is.True);
        Assert.That(actual, Is.EqualTo(["/repo/one", "/repo/two"]));
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("   \n  \n  ", false)]
    [TestCase(null, true)]
    [TestCase("", true)]
    [TestCase("   \n  \n  ", true)]
    public async Task CachedRepositoryPaths_MissingOrBlank_ReturnsNull(string? content, bool loadAsync)
    {
        var path = GitWizardApi.GetCachedRepositoryListPath();
        if (content is not null)
        {
            await File.WriteAllTextAsync(path, content);
        }

        var actual = loadAsync
            ? await GitWizardApi.GetCachedRepositoryPathsAsync()
            : LoadSync();
        Assert.That(actual, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SaveCachedRepositoryPaths_CreatesDirectory_WhenHomeDeleted(bool async)
    {
        var localPath = GitWizardApi.GetLocalFilesPath();
        if (Directory.Exists(localPath))
        {
            Directory.Delete(localPath, recursive: true);
        }

        if (async)
        {
            await GitWizardApi.SaveCachedRepositoryPathsAsync(["/test"]);
        }
        else
        {
            SaveSync(["/test"]);
        }

        Assert.That(Directory.Exists(localPath), Is.True);
        Assert.That(File.Exists(GitWizardApi.GetCachedRepositoryListPath()), Is.True);
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
    public void GetLocalFilesSize_ReturnsZero_WhenFolderDoesNotExist()
    {
        var localPath = GitWizardApi.GetLocalFilesPath();
        if (Directory.Exists(localPath))
        {
            Directory.Delete(localPath, recursive: true);
        }

        var size = GitWizardApi.GetLocalFilesSize();
        Assert.That(size, Is.EqualTo(0L));
    }

    [Test]
    public void GetLocalFilesSize_ReturnsNonNegative()
    {
        var size = GitWizardApi.GetLocalFilesSize();
        Assert.That(size, Is.GreaterThanOrEqualTo(0L));
    }

    [Test]
    public void EnsureLocalFolderExists_Idempotent()
    {
        GitWizardApi.EnsureLocalFolderExists();
        Assert.That(Directory.Exists(GitWizardApi.GetLocalFilesPath()), Is.True);
        GitWizardApi.EnsureLocalFolderExists();
        Assert.That(Directory.Exists(GitWizardApi.GetLocalFilesPath()), Is.True);
    }

    [Test]
    public void GetLogFolderPath_ContainsLogsAndLocalFilesPath()
    {
        var localPath = GitWizardApi.GetLocalFilesPath();
        var logPath = GitWizardApi.GetLogFolderPath();
        Assert.Multiple(() =>
        {
            Assert.That(logPath, Does.Contain("Logs"));
            Assert.That(logPath, Does.Contain(localPath));
        });
    }

    [Test]
    public void GetCachedRepositoryListPath_ReturnsCorrectPath()
    {
        var localPath = GitWizardApi.GetLocalFilesPath();
        var cachedPath = GitWizardApi.GetCachedRepositoryListPath();
        Assert.That(cachedPath, Does.EndWith(Path.Combine(localPath, "repositories.txt")));
    }
}
