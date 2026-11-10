using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

[NonParallelizable]
public class GitWizardConfigurationTests
{
    LocalFilesSandbox? _sandbox;
    string? _tempRoot;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;

        // Redirect the data dir to a temp folder so GetGlobalConfigurationPath() resolves there,
        // NOT the real ~/.GitWizard. Without this, SaveGlobalConfiguration_SavesConfiguration writes
        // "/global/test" to the user's real config.json (and the Get*GlobalConfiguration tests read
        // it). Mirrors the sibling classes (SettingsViewModelTests, AsyncFileIOTests,
        // GitWizardReportAdditionalTests). LocalFilesSandbox creates the temp dir, so it also
        // serves as _tempRoot for the explicit-path Save_* tests.
        _sandbox = new LocalFilesSandbox();
        _tempRoot = _sandbox.Path;
    }

    [TearDown]
    public void TearDown()
    {
        _sandbox?.Dispose();
        _sandbox = null;
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
        {
            Assert.Ignore("Unix-only: Windows default search path is %USERPROFILE% (see Windows companion).");
        }

        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        Assert.That(config.SearchPaths, Does.Contain("~"));
    }

    [Test]
    public void CreateDefaultConfiguration_SearchPath_ContainsUserProfile_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows-only: Unix default search path is ~ (see Unix companion).");
        }

        var config = GitWizardConfiguration.CreateDefaultConfiguration();
        Assert.That(config.SearchPaths, Does.Contain("%USERPROFILE%"));
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

    public sealed record RoundTripCase(
        string Name,
        Action<GitWizardConfiguration> Configure,
        Func<GitWizardConfiguration, object?> Read,
        string ExpectedJsonKey,
        string? ExpectedJsonValue,
        object? ExpectedValue)
    {
        public override string ToString() => Name;
    }

    static IEnumerable<RoundTripCase> RoundTripCases()
    {
        yield return new RoundTripCase("ForkPath",
            config => config.ForkPath = "/custom/fork",
            config => config.ForkPath,
            "\"forkPath\"", null, "/custom/fork");
        yield return new RoundTripCase("SearchPaths",
            config => config.SearchPaths.Add("/path/one"),
            config => config.SearchPaths,
            "\"SearchPaths\"", null, new SortedSet<string> { "/path/one" });
        yield return new RoundTripCase("IgnoredPaths",
            config => config.IgnoredPaths.Add("/ignored/one"),
            config => config.IgnoredPaths,
            "\"IgnoredPaths\"", null, new SortedSet<string> { "/ignored/one" });
        yield return new RoundTripCase("SkipHiddenDirectoriesTrue",
            config => config.SkipHiddenDirectories = true,
            config => config.SkipHiddenDirectories,
            "\"skipHiddenDirectories\"", "true", true);
        yield return new RoundTripCase("SkipHiddenDirectoriesFalse",
            config => config.SkipHiddenDirectories = false,
            config => config.SkipHiddenDirectories,
            "\"skipHiddenDirectories\"", "false", false);
        yield return new RoundTripCase("IncludeTempDirectoriesTrue",
            config => config.IncludeTempDirectories = true,
            config => config.IncludeTempDirectories,
            "\"includeTempDirectories\"", "true", true);
        yield return new RoundTripCase("IncludeTempDirectoriesFalse",
            config => config.IncludeTempDirectories = false,
            config => config.IncludeTempDirectories,
            "\"includeTempDirectories\"", "false", false);
    }

    [TestCaseSource(nameof(RoundTripCases))]
    public void Save_RoundTripsProperty(RoundTripCase roundTripCase)
    {
        var config = new GitWizardConfiguration();
        roundTripCase.Configure(config);
        var jsonPath = Path.Combine(_tempRoot!, "round_trip.json");

        config.Save(jsonPath);
        var json = File.ReadAllText(jsonPath);
        var loaded = GitWizardConfiguration.GetConfigurationAtPath(jsonPath);

        Assert.That(loaded, Is.Not.Null, "GetConfigurationAtPath returned null for a saved configuration.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(json, Does.Contain(roundTripCase.ExpectedJsonKey));
            if (roundTripCase.ExpectedJsonValue is not null)
            {
                Assert.That(json, Does.Contain(roundTripCase.ExpectedJsonValue));
            }

            Assert.That(roundTripCase.Read(loaded!), Is.EqualTo(roundTripCase.ExpectedValue));
        }
    }

    [Test]
    public void Save_OmitsNullProperties()
    {
        var config = new GitWizardConfiguration();
        var jsonPath = Path.Combine(_tempRoot!, "null_omission.json");

        config.Save(jsonPath);

        var json = File.ReadAllText(jsonPath);
        // DefaultIgnoreCondition = WhenWritingDefault omits null (the default for the
        // nullable properties), and reading the file back pins that Save created it.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json, Does.Not.Contain("forkPath"));
            Assert.That(json, Does.Not.Contain("skipHiddenDirectories"));
            Assert.That(json, Does.Not.Contain("includeTempDirectories"));
        }
    }

    [TestCase(2)]
    [TestCase(4)]
    public void Save_CreatesMissingDirectories(int depth)
    {
        var config = new GitWizardConfiguration();
        var segments = Enumerable.Range(1, depth).Select(level => $"level{level}").ToArray();
        var directory = Path.Combine([_tempRoot!, .. segments]);
        var jsonPath = Path.Combine(directory, "config.json");

        config.Save(jsonPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.Exists(directory), Is.True);
            Assert.That(File.Exists(jsonPath), Is.True);
        }
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
    public async Task GetGlobalConfigurationAsync_CompletesSynchronously_WhenAlreadyLoaded()
    {
        var loadedConfiguration = await GitWizardConfiguration.GetGlobalConfigurationAsync();

        var task = GitWizardConfiguration.GetGlobalConfigurationAsync();

        Assert.That(task.IsCompletedSuccessfully, Is.True,
            "The cached configuration path should complete synchronously without I/O.");
        var result = await task;
        Assert.That(result, Is.SameAs(loadedConfiguration));
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
    public void AddIgnoredPath_KeepsSorted()
    {
        var config = new GitWizardConfiguration();
        config.IgnoredPaths.Add("/c/path");
        config.IgnoredPaths.Add("/a/path");
        config.IgnoredPaths.Add("/b/path");

        var paths = config.IgnoredPaths.ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(paths[0], Is.EqualTo("/a/path"));
            Assert.That(paths[1], Is.EqualTo("/b/path"));
            Assert.That(paths[2], Is.EqualTo("/c/path"));
        }
    }

    [Test]
    public void AddSearchPath_KeepsSorted()
    {
        var config = new GitWizardConfiguration();
        config.SearchPaths.Add("/c/search");
        config.SearchPaths.Add("/a/search");
        config.SearchPaths.Add("/b/search");

        var paths = config.SearchPaths.ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(paths[0], Is.EqualTo("/a/search"));
            Assert.That(paths[1], Is.EqualTo("/b/search"));
            Assert.That(paths[2], Is.EqualTo("/c/search"));
        }
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

