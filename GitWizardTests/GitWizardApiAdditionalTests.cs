using GitWizard;

namespace GitWizardTests;

public class GitWizardApiAdditionalTests
{
    string? _tempRoot;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _tempRoot = Path.Combine(Path.GetTempPath(), "GitWizardApiAdditionalTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (!string.IsNullOrEmpty(_tempRoot) && Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public void GetLocalFilesPath_ReturnsNonEmpty()
    {
        var path = GitWizardApi.GetLocalFilesPath();
        Assert.That(path, Is.Not.Null);
        Assert.That(path, Is.Not.Empty);
    }

    [Test]
    public void GetLocalFilesPath_ContainsUserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = GitWizardApi.GetLocalFilesPath();
        Assert.That(path, Does.Contain(profile));
    }

    [Test]
    public void GetCachedRepositoryListPath_ReturnsCorrectPath()
    {
        var localPath = GitWizardApi.GetLocalFilesPath();
        var cachedPath = GitWizardApi.GetCachedRepositoryListPath();
        Assert.That(cachedPath, Does.EndWith(Path.Combine(localPath, "repositories.txt")));
    }

    [Test]
    public void GetLogFolderPath_ReturnsCorrectPath()
    {
        var localPath = GitWizardApi.GetLocalFilesPath();
        var logPath = GitWizardApi.GetLogFolderPath();
        Assert.That(logPath, Does.Contain(localPath));
        Assert.That(logPath, Does.Contain("Logs"));
    }

    [Test]
    public void EnsureLocalFolderExists_CreatesDirectory()
    {
        GitWizardApi.EnsureLocalFolderExists();
        Assert.That(Directory.Exists(GitWizardApi.GetLocalFilesPath()), Is.True);
    }

    [Test]
    public void ExpandSearchPath_ReturnsNull_ForNonExistentDirectory()
    {
        var result = GitWizardApi.ExpandSearchPath("/nonexistent/path/that/does/not/exist");
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ExpandSearchPath_ReturnsExpandedPath_ForExistingDirectory()
    {
        var testDir = Path.Combine(_tempRoot!, "testdir");
        Directory.CreateDirectory(testDir);

        var result = GitWizardApi.ExpandSearchPath(testDir);
        Assert.That(result, Is.Not.Null);
        // ExpandSearchPath normalizes via NormalizePath, which lower-cases on
        // Windows (matching GitWizardApi.PathComparison = OrdinalIgnoreCase), so
        // compare case-insensitively rather than asserting the input's casing.
        Assert.That(result, Is.EqualTo(testDir).IgnoreCase);
    }

    [Test]
    public void ExpandSearchPath_ExpandsEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("TEST_EXPAND_PATH", _tempRoot!);
        try
        {
            var result = GitWizardApi.ExpandSearchPath("%TEST_EXPAND_PATH%");
            Assert.That(result, Is.Not.Null);
            // Case-insensitive: NormalizePath lower-cases on Windows (see above).
            Assert.That(result, Is.EqualTo(_tempRoot).IgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_EXPAND_PATH", null);
        }
    }

    [Test]
    public void ExpandSearchPath_ReturnsUserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = GitWizardApi.ExpandSearchPath(profile);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void PrettyPrintPath_ReturnsOriginalPath_WhenNoEnvVars()
    {
        var path = "/absolute/path/without/env";
        var result = GitWizardApi.PrettyPrintPath(path);
        Assert.That(result, Is.EqualTo(path));
    }

    [Test]
    public void PrettyPrintPath_HandlesEmptyPath()
    {
        var result = GitWizardApi.PrettyPrintPath("");
        Assert.That(result, Is.EqualTo(""));
    }

    [Test]
    public void SaveCachedRepositoryPaths_CreatesFile()
    {
        var paths = new[] { "/repo/1", "/repo/2" };
        GitWizardApi.SaveCachedRepositoryPaths(paths);

        var cachedPath = GitWizardApi.GetCachedRepositoryListPath();
        Assert.That(File.Exists(cachedPath), Is.True);
    }

    [Test]
    public void GetCachedRepositoryPaths_ReturnsPaths()
    {
        var paths = new[] { "/repo/1", "/repo/2" };
        GitWizardApi.SaveCachedRepositoryPaths(paths);

        var result = GitWizardApi.GetCachedRepositoryPaths();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!, Has.Length.EqualTo(2));
        Assert.That(result, Does.Contain("/repo/1"));
        Assert.That(result, Does.Contain("/repo/2"));
    }

    [Test]
    public void GetCachedRepositoryPaths_ReturnsNull_ForEmptyFile()
    {
        var cachedPath = GitWizardApi.GetCachedRepositoryListPath();
        File.WriteAllText(cachedPath, "");

        var result = GitWizardApi.GetCachedRepositoryPaths();
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetCachedRepositoryPaths_ReturnsNull_WhenFileDoesNotExist()
    {
        var cachedPath = GitWizardApi.GetCachedRepositoryListPath();
        if (File.Exists(cachedPath))
            File.Delete(cachedPath);

        var result = GitWizardApi.GetCachedRepositoryPaths();
        Assert.That(result, Is.Null);
    }

    [Test]
    public void SaveCachedRepositoryPaths_CreatesDirectoryIfNeeded()
    {
        GitWizardApi.SaveCachedRepositoryPaths(new[] { "/test" });
        Assert.That(Directory.Exists(GitWizardApi.GetLocalFilesPath()), Is.True);
    }

    [Test]
    public void ClearCache_DoesNotThrowWhenNoFiles()
    {
        GitWizardApi.ClearCache();
        Assert.Pass();
    }

    [Test]
    public void DeleteAllLocalFiles_DoesNotThrow()
    {
        GitWizardApi.DeleteAllLocalFiles();
        Assert.Pass();
    }

    // Regression guard for the data-loss incident: this class calls DeleteAllLocalFiles()/ClearCache()
    // without a per-class redirect, so without the assembly-wide GITWIZARD_HOME net (GlobalTestSetup)
    // GetLocalFilesPath() resolves to the real ~/.GitWizard and DeleteAllLocalFiles wipes the user's
    // config. This fails loudly if that net is ever removed.
    [Test]
    public void GetLocalFilesPath_NeverResolvesToTheRealGitWizardDirectoryDuringTests()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".GitWizard");
        Assert.That(GitWizardApi.GetLocalFilesPath(), Is.Not.EqualTo(real),
            "Tests must resolve local files to an isolated temp home - a real-dir DeleteAllLocalFiles wipes the user's config.");
    }

    [Test]
    public void GetRepositoryPaths_HandlesNonExistentRootPath()
    {
        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths("/nonexistent/root/path", paths, Array.Empty<string>());
        Assert.That(paths, Is.Empty);
    }

    [Test]
    public void GetRepositoryPaths_FindsRepoInSubdirectory()
    {
        var root = Path.Combine(_tempRoot!, "root");
        var subRepo = Path.Combine(root, "sub", "repo");
        Directory.CreateDirectory(Path.Combine(subRepo, ".git"));

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>());

        Assert.That(paths, Has.Count.EqualTo(1));
        // Use the platform separator: GetRepositoryPaths returns OS-native paths
        // (backslash on Windows), not the literal forward slash.
        Assert.That(paths.First(), Does.Contain(Path.Combine("sub", "repo")));
    }

    [Test]
    public void GetRepositoryPaths_IgnoresDotDirectories()
    {
        var root = Path.Combine(_tempRoot!, "root");
        var dotDir = Path.Combine(root, ".hidden", "repo", ".git");
        Directory.CreateDirectory(dotDir);

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>());

        Assert.That(paths, Is.Empty);
    }

    [Test]
    public void GetRepositoryPaths_FindsMultipleRepos()
    {
        var root = Path.Combine(_tempRoot!, "root");
        var repo1 = Path.Combine(root, "repo1", ".git");
        var repo2 = Path.Combine(root, "repo2", ".git");
        var repo3 = Path.Combine(root, "repo3", ".git");
        Directory.CreateDirectory(repo1);
        Directory.CreateDirectory(repo2);
        Directory.CreateDirectory(repo3);

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>());

        Assert.That(paths, Has.Count.EqualTo(3));
    }

    [Test]
    public void GetRepositoryPaths_IgnoresIgnoredPaths()
    {
        var root = Path.Combine(_tempRoot!, "root");
        var ignored = Path.Combine(root, "ignored", "repo", ".git");
        var included = Path.Combine(root, "included", "repo", ".git");
        Directory.CreateDirectory(ignored);
        Directory.CreateDirectory(included);

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, new[] { Path.Combine(root, "ignored", "repo") });

        Assert.That(paths, Has.Count.EqualTo(1));
        Assert.That(paths.First(), Does.EndWith(Path.Combine("included", "repo")));
    }

    [Test]
    public void GetRepositoryPaths_SkipsDirectoriesStartingWithDot()
    {
        var root = Path.Combine(_tempRoot!, "root");
        var dotDir = Path.Combine(root, ".hidden", "repo");
        var normalDir = Path.Combine(root, "normal", "repo");
        Directory.CreateDirectory(Path.Combine(dotDir, ".git"));
        Directory.CreateDirectory(Path.Combine(normalDir, ".git"));

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>());

        // .hidden directory starts with dot so its children are skipped
        // Only normal/repo is found
        Assert.That(paths, Has.Count.EqualTo(1));
        Assert.That(paths.First(), Does.Contain("normal"));
    }

    [Test]
    public void GetRepositoryPaths_FindsReposInDotDirectories_WhenSkipHiddenDirectoriesFalse()
    {
        var root = Path.Combine(_tempRoot!, "root");
        var dotDir = Path.Combine(root, ".hidden", "repo");
        var normalDir = Path.Combine(root, "normal", "repo");
        Directory.CreateDirectory(Path.Combine(dotDir, ".git"));
        Directory.CreateDirectory(Path.Combine(normalDir, ".git"));

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>(), skipHiddenDirectories: false);

        Assert.That(paths, Has.Count.EqualTo(2));
        // GetRepositoryPaths normalizes (lower-cases) returned paths on Windows, so compare
        // case-insensitively rather than against dotDir/normalDir's original casing.
        Assert.That(paths, Does.Contain(dotDir).IgnoreCase);
        Assert.That(paths, Does.Contain(normalDir).IgnoreCase);
    }

    [Test]
    public void GetRepositoryPaths_SkipsDotDirectories_WhenSkipHiddenDirectoriesTrue()
    {
        var root = Path.Combine(_tempRoot!, "root");
        var dotDir = Path.Combine(root, ".hidden", "repo");
        var normalDir = Path.Combine(root, "normal", "repo");
        Directory.CreateDirectory(Path.Combine(dotDir, ".git"));
        Directory.CreateDirectory(Path.Combine(normalDir, ".git"));

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>(), skipHiddenDirectories: true);

        Assert.That(paths, Has.Count.EqualTo(1));
        Assert.That(paths.First(), Does.Contain("normal"));
    }

    [Test]
    public void GetRepositoryPaths_SkipsDotDirectories_WhenSkipHiddenDirectoriesNull()
    {
        var root = Path.Combine(_tempRoot!, "root");
        var dotDir = Path.Combine(root, ".hidden", "repo");
        var normalDir = Path.Combine(root, "normal", "repo");
        Directory.CreateDirectory(Path.Combine(dotDir, ".git"));
        Directory.CreateDirectory(Path.Combine(normalDir, ".git"));

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>(), skipHiddenDirectories: null);

        // Null = default = skip dot-prefixed directories (backward-compatible behavior)
        Assert.That(paths, Has.Count.EqualTo(1));
        Assert.That(paths.First(), Does.Contain("normal"));
    }

    [Test]
    public async Task GetCachedRepositoryPathsAsync_ReturnsCachedPaths()
    {
        var paths = new[] { "/async/repo/1", "/async/repo/2" };
        GitWizardApi.SaveCachedRepositoryPaths(paths);

        var result = await GitWizardApi.GetCachedRepositoryPathsAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!, Has.Length.EqualTo(2));
    }

    [Test]
    public async Task SaveCachedRepositoryPathsAsync_CreatesFile()
    {
        var paths = new[] { "/async/save/repo" };
        await GitWizardApi.SaveCachedRepositoryPathsAsync(paths);

        var cachedPath = GitWizardApi.GetCachedRepositoryListPath();
        Assert.That(File.Exists(cachedPath), Is.True);
    }

    [Test]
    public void GetLocalFilesSize_ReturnsNonNegative()
    {
        var size = GitWizardApi.GetLocalFilesSize();
        Assert.That(size, Is.GreaterThanOrEqualTo(0L));
    }

    [Test]
    public void GetLocalFilesSize_ReturnsZero_WhenFolderDoesNotExist()
    {
        GitWizardApi.DeleteAllLocalFiles();
        var size = GitWizardApi.GetLocalFilesSize();
        Assert.That(size, Is.EqualTo(0L));
    }

    [Test]
    public void GetRepositoryPaths_WithUpdateHandler_ReportsCount()
    {
        var root = Path.Combine(_tempRoot!, "root");
        var subRepo = Path.Combine(root, "repo", ".git");
        Directory.CreateDirectory(subRepo);

        var paths = new SortedSet<string>();
        var handler = new TestUpdateHandler();
        GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>(), handler);

        Assert.That(paths, Has.Count.EqualTo(1));
    }

    [Test]
    public void GetRepositoryPaths_WithNonExistentIgnoredPath_DoesNotThrow()
    {
        var root = Path.Combine(_tempRoot!, "root");
        Directory.CreateDirectory(root);

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, new[] { "/nonexistent/ignored/path" });

        Assert.That(paths, Is.Empty);
    }

    [Test]
    public void GetRepositoryPaths_FindsGitFileSubmodule()
    {
        var root = Path.Combine(_tempRoot!, "root");
        var submodulePath = Path.Combine(root, "submodule");
        Directory.CreateDirectory(submodulePath);
        File.WriteAllText(Path.Combine(submodulePath, ".git"), "gitdir: /some/path");

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>());

        Assert.That(paths, Has.Count.EqualTo(1));
    }

    [Test]
    public void GetRepositoryPaths_IncludesGitFiles_WhenNotDriveRoot()
    {
        var root = Path.Combine(_tempRoot!, "root");
        var subDir = Path.Combine(root, "subdir");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, ".git"), "gitdir: /some/path");

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>());

        Assert.That(paths, Has.Count.EqualTo(1));
    }

    [Test]
    public void TryFindAllRepositoriesUsingMft_ReturnsFalse_WhenNoMftFlagDisablesScan()
    {
        var found = GitWizardApi.TryFindAllRepositoriesUsingMft(
            new GitWizardConfiguration(), new SortedSet<string>(), noMft: true);

        Assert.That(found, Is.False);
    }

    [Test]
    public void TryFindAllRepositoriesUsingMft_ReturnsFalse_OnNonWindows()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("MFT scanning is Windows-only; the non-Windows early-return is only reachable off Windows.");

        var found = GitWizardApi.TryFindAllRepositoriesUsingMft(
            new GitWizardConfiguration(), new SortedSet<string>());

        Assert.That(found, Is.False);
    }

    [Test]
    public void GetRepositoryPaths_FindsArbitrarilyNestedDotGitDirectories()
    {
        var root = Path.Combine(_tempRoot!, "root");
        Directory.CreateDirectory(Path.Combine(root, "a", "b", "c", ".git"));
        Directory.CreateDirectory(Path.Combine(root, "x", ".git"));

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>());

        Assert.That(paths, Has.Count.EqualTo(2));
    }

    [Test]
    public void GetRepositoryPaths_FindsEverySiblingRepo()
    {
        var root = Path.Combine(_tempRoot!, "root");
        for (var i = 0; i < 10; i++)
            Directory.CreateDirectory(Path.Combine(root, $"repo{i}", ".git"));

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>());

        Assert.That(paths, Has.Count.EqualTo(10));
    }

    [Test]
    public void ClearCache_DeletesCachedRepositoryListAndReport()
    {
        GitWizardApi.EnsureLocalFolderExists();
        File.WriteAllText(GitWizardApi.GetCachedRepositoryListPath(), "path1\npath2");
        File.WriteAllText(GitWizardReport.GetCachedReportPath(), "{}");

        GitWizardApi.ClearCache();

        Assert.That(File.Exists(GitWizardApi.GetCachedRepositoryListPath()), Is.False);
        Assert.That(File.Exists(GitWizardReport.GetCachedReportPath()), Is.False);
    }

    [Test]
    public async Task SaveCachedRepositoryPathsAsync_RoundTripsThroughGetCachedPaths()
    {
        await GitWizardApi.SaveCachedRepositoryPathsAsync(new[] { "/async/repo/1", "/async/repo/2" });

        var reloaded = await GitWizardApi.GetCachedRepositoryPathsAsync();

        Assert.That(reloaded, Is.Not.Null);
        Assert.That(reloaded!, Has.Length.EqualTo(2));
    }
}

public class TestUpdateHandler : IUpdateHandler
{
    public void StartProgress(string description, int total) { }

    public void UpdateProgress(int count) { }

    public void SendUpdateMessage(string? message) { }

    public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
    public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
    public void OnWorktreeCreated(GitWizardRepository worktree) { }
}
