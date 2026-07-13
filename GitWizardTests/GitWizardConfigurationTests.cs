using GitWizard;

namespace GitWizardTests;

public class GitWizardConfigurationTests
{
    string? _tempRoot;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;

        // Redirect the data dir to a temp folder so GetGlobalConfigurationPath() resolves there,
        // NOT the real ~/.GitWizard. Without this, SaveGlobalConfiguration_SavesConfiguration writes
        // "/global/test" to the user's real config.json (and the Get*GlobalConfiguration tests read
        // it). Mirrors the sibling classes (SettingsViewModelTests, AsyncFileIOTests,
        // GitWizardReportAdditionalTests). RedirectLocalFilesToTemp creates the temp dir, so it also
        // serves as _tempRoot for the explicit-path Save_* tests.
        _tempRoot = TestUtilities.RedirectLocalFilesToTemp();

        TestUtilities.ResetStaticCaches();
    }

    [TearDown]
    public void TearDown()
    {
        TestUtilities.ClearLocalFilesRedirect(_tempRoot);

        TestUtilities.ResetStaticCaches();
    }

    [Test]
    public void CreateDefaultConfiguration_HasSearchPaths()
    {
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        Assert.That(config.SearchPaths, Has.Count.GreaterThan(0));
    }

    [Test]
    public void CreateDefaultConfiguration_HasIgnoredPaths_OnWindows()
    {
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        Assert.That(config, Is.Not.Null);
        // On Windows, default ignored paths include common dev directories
        Assert.That(config.IgnoredPaths, Is.Not.Null);
    }

    [Test]
    public void CreateDefaultConfiguration_SearchPath_ContainsHome_OnUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Unix-only: Windows default search path is %USERPROFILE% (see Windows companion).");

        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        Assert.That(config.SearchPaths, Does.Contain("~"));
    }

    [Test]
    public void CreateDefaultConfiguration_SearchPath_ContainsUserProfile_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Windows-only: Unix default search path is ~ (see Unix companion).");

        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        Assert.That(config.SearchPaths, Does.Contain("%USERPROFILE%"));
    }

    [Test]
    public void Save_CreatesDirectoryIfNeeded()
    {
        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        var nestedPath = Path.Combine(_tempRoot!, "sub", "dir", "config.json");

        config.Save(nestedPath);

        Assert.That(File.Exists(nestedPath), Is.True);
    }

    [Test]
    public void Save_WritesValidJson()
    {
        var config = new GitWizardConfiguration
        {
            SearchPaths = { "/custom/search" },
            ForkPath = "/usr/local/bin/fork"
        };
        var jsonPath = Path.Combine(_tempRoot!, "test_config.json");

        config.Save(jsonPath);

        var json = File.ReadAllText(jsonPath);
        Assert.That(json, Does.Contain("custom/search"));
    }

    [Test]
    public void Save_SerializesForkPath()
    {
        var config = new GitWizardConfiguration
        {
            ForkPath = "/custom/fork"
        };
        var jsonPath = Path.Combine(_tempRoot!, "fork_test.json");

        config.Save(jsonPath);

        var json = File.ReadAllText(jsonPath);
        Assert.That(json, Does.Contain("forkPath"));
    }

    [Test]
    public void Save_SerializesSearchPaths()
    {
        var config = new GitWizardConfiguration
        {
            SearchPaths = { "/path/one", "/path/two" }
        };
        var jsonPath = Path.Combine(_tempRoot!, "search_test.json");

        config.Save(jsonPath);

        var json = File.ReadAllText(jsonPath);
        Assert.That(json, Does.Contain("\"SearchPaths\""));
    }

    [Test]
    public void Save_SerializesIgnoredPaths()
    {
        var config = new GitWizardConfiguration
        {
            IgnoredPaths = { "/ignored/one" }
        };
        var jsonPath = Path.Combine(_tempRoot!, "ignore_test.json");

        config.Save(jsonPath);

        var json = File.ReadAllText(jsonPath);
        Assert.That(json, Does.Contain("\"IgnoredPaths\""));
    }

    [Test]
    public void Save_SerializesSkipHiddenDirectories_True()
    {
        var config = new GitWizardConfiguration
        {
            SkipHiddenDirectories = true
        };
        var jsonPath = Path.Combine(_tempRoot!, "skip_hidden_true.json");

        config.Save(jsonPath);

        var json = File.ReadAllText(jsonPath);
        Assert.That(json, Does.Contain("skipHiddenDirectories"));
        Assert.That(json, Does.Contain("true"));
    }

    [Test]
    public void Save_OmitsSkipHiddenDirectories_WhenNull()
    {
        var config = new GitWizardConfiguration();
        var jsonPath = Path.Combine(_tempRoot!, "skip_hidden_default.json");

        config.Save(jsonPath);

        var json = File.ReadAllText(jsonPath);
        // DefaultIgnoreCondition = WhenWritingDefault omits null (the default for bool?)
        Assert.That(json, Does.Not.Contain("skipHiddenDirectories"));
    }

    [Test]
    public void GetConfigurationAtPath_DeserializesSkipHiddenDirectories_True()
    {
        var config = new GitWizardConfiguration { SkipHiddenDirectories = true };
        var jsonPath = Path.Combine(_tempRoot!, "load_skip_hidden_true.json");
        config.Save(jsonPath);

        var loaded = GitWizardConfiguration.GetConfigurationAtPath(jsonPath);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.SkipHiddenDirectories, Is.True);
    }

    [Test]
    public void GetConfigurationAtPath_DeserializesSkipHiddenDirectories_False()
    {
        var config = new GitWizardConfiguration { SkipHiddenDirectories = false };
        var jsonPath = Path.Combine(_tempRoot!, "load_skip_hidden_false.json");
        config.Save(jsonPath);

        var loaded = GitWizardConfiguration.GetConfigurationAtPath(jsonPath);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.SkipHiddenDirectories, Is.False);
    }

    [Test]
    public void Save_SerializesNullForkPath_IgnoresNull()
    {
        var config = new GitWizardConfiguration
        {
            ForkPath = null
        };
        var jsonPath = Path.Combine(_tempRoot!, "null_fork.json");

        config.Save(jsonPath);

        var json = File.ReadAllText(jsonPath);
        // ForkPath is null and should be omitted (DefaultIgnoreCondition = WhenWritingDefault)
        Assert.That(json, Does.Not.Contain("forkPath"));
    }

    [Test]
    public void Save_CreatesNestedDirectoryStructure()
    {
        var config = new GitWizardConfiguration();
        var deepPath = Path.Combine(_tempRoot!, "a", "b", "c", "d", "config.json");

        config.Save(deepPath);

        Assert.That(Directory.Exists(Path.Combine(_tempRoot!, "a", "b", "c", "d")), Is.True);
    }

    [Test]
    public void GetConfigurationAtPath_ReturnsNull_ForNonExistentFile()
    {
        var nonexistentPath = Path.Combine(_tempRoot!, "nonexistent.json");
        var result = GitWizardConfiguration.GetConfigurationAtPath(nonexistentPath);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetConfigurationAtPath_ReturnsNull_ForInvalidJson()
    {
        var invalidPath = Path.Combine(_tempRoot!, "invalid.json");
        File.WriteAllText(invalidPath, "not json {{{");

        var result = GitWizardConfiguration.GetConfigurationAtPath(invalidPath);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetConfigurationAtPathAsync_ReturnsNull_ForInvalidJson()
    {
        var invalidPath = Path.Combine(_tempRoot!, "invalid-async.json");
        await File.WriteAllTextAsync(invalidPath, "not json {{{");

        var result = await GitWizardConfiguration.GetConfigurationAtPathAsync(invalidPath);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void SaveGlobalConfiguration_SavesConfiguration()
    {
        var config = new GitWizardConfiguration
        {
            SearchPaths = { "/global/test" }
        };

        GitWizardConfiguration.SaveGlobalConfiguration(config);

        var loaded = GitWizardConfiguration.GetGlobalConfiguration();
        Assert.That(loaded.SearchPaths, Does.Contain("/global/test"));
    }

    [Test]
    public void GetGlobalConfiguration_ReturnsSameInstance()
    {
        var first = GitWizardConfiguration.GetGlobalConfiguration();
        var second = GitWizardConfiguration.GetGlobalConfiguration();

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public async Task GetGlobalConfigurationAsync_ReturnsSameInstance()
    {
        var first = await GitWizardConfiguration.GetGlobalConfigurationAsync();
        var second = await GitWizardConfiguration.GetGlobalConfigurationAsync();

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public async Task GetGlobalConfigurationAsync_DoesNotBlock_WhenAlreadyLoaded()
    {
        var _ = await GitWizardConfiguration.GetGlobalConfigurationAsync();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var task = GitWizardConfiguration.GetGlobalConfigurationAsync();
        await task;
        stopwatch.Stop();

        // Should return immediately (cached, no I/O)
        Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(100));
    }

    [Test]
    public void ForkPath_ProducesJsonValue()
    {
        var config = new GitWizardConfiguration { ForkPath = "/fork/exe" };
        var jsonPath = Path.Combine(_tempRoot!, "forkpath_test.json");
        config.Save(jsonPath);

        var json = File.ReadAllText(jsonPath);
        Assert.That(json, Does.Contain("forkPath"));
    }

    [Test]
    public void Save_EmptyConfiguration()
    {
        var config = new GitWizardConfiguration();
        var jsonPath = Path.Combine(_tempRoot!, "empty.json");

        config.Save(jsonPath);

        Assert.That(File.Exists(jsonPath), Is.True);
    }

    [Test]
    public void SearchPaths_IsSortedSet()
    {
        var config = new GitWizardConfiguration();
        Assert.That(config.SearchPaths, Is.InstanceOf<SortedSet<string>>());
    }

    [Test]
    public void IgnoredPaths_IsSortedSet()
    {
        var config = new GitWizardConfiguration();
        Assert.That(config.IgnoredPaths, Is.InstanceOf<SortedSet<string>>());
    }

    [Test]
    public void GetGlobalConfigurationPath_ReturnsNonEmpty()
    {
        var path = GitWizardConfiguration.GetGlobalConfigurationPath();
        Assert.That(path, Is.Not.Null);
        Assert.That(path, Is.Not.Empty);
        Assert.That(path, Does.EndWith("config.json"));
    }

    [Test]
    public void Save_CreatesDirectoryStructure()
    {
        var config = new GitWizardConfiguration();
        var nestedPath = Path.Combine(_tempRoot!, "sub", "dir", "config.json");

        config.Save(nestedPath);

        Assert.That(File.Exists(nestedPath), Is.True);
    }

    [Test]
    public void AddIgnoredPath_KeepsSorted()
    {
        var config = new GitWizardConfiguration();
        config.IgnoredPaths.Add("/c/path");
        config.IgnoredPaths.Add("/a/path");
        config.IgnoredPaths.Add("/b/path");

        var paths = config.IgnoredPaths.ToList();
        Assert.That(paths[0], Is.EqualTo("/a/path"));
        Assert.That(paths[1], Is.EqualTo("/b/path"));
        Assert.That(paths[2], Is.EqualTo("/c/path"));
    }

    [Test]
    public void AddSearchPath_KeepsSorted()
    {
        var config = new GitWizardConfiguration();
        config.SearchPaths.Add("/c/search");
        config.SearchPaths.Add("/a/search");
        config.SearchPaths.Add("/b/search");

        var paths = config.SearchPaths.ToList();
        Assert.That(paths[0], Is.EqualTo("/a/search"));
        Assert.That(paths[1], Is.EqualTo("/b/search"));
        Assert.That(paths[2], Is.EqualTo("/c/search"));
    }

    [Test]
    public void GetConfigurationAtPath_ReturnsLoadedConfiguration()
    {
        TestUtilities.ResetStaticCaches();

        var configPath = Path.Combine(_tempRoot!, "load_test.json");
        var config = new GitWizardConfiguration { SearchPaths = { "/dup" } };
        config.Save(configPath);

        var loaded = GitWizardConfiguration.GetConfigurationAtPath(configPath);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.SearchPaths, Does.Contain("/dup"));
    }
}
