using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

public class GitWizardApiTests
{
    TempDirectory? _tempDirectory;
    string? _tempRoot;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _tempDirectory = new TempDirectory("GitWizardApiTests");
        _tempRoot = _tempDirectory.Path;
    }

    [TearDown]
    public void TearDown()
    {
        _tempDirectory?.Dispose();
    }

    [Test]
    public void GetLocalFilesPath_ReturnsNonEmpty()
    {
        var path = GitWizardApi.GetLocalFilesPath();
        Assert.That(path, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void GetLocalFilesPath_ContainsUserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = GitWizardApi.GetLocalFilesPath();
        Assert.That(path, Does.Contain(profile));
    }

    [Test]
    public void GetLocalFilesPath_NeverResolvesToTheRealGitWizardDirectoryDuringTests()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".GitWizard");
        Assert.That(GitWizardApi.GetLocalFilesPath(), Is.Not.EqualTo(real),
            "Tests must resolve local files to an isolated temp home - a real-dir TryDeleteAllLocalFiles wipes the user's config.");
    }

    [Test]
    public void PrettyPrintPath_ExpandsEnvironmentVariables()
    {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Environment.SetEnvironmentVariable("HOME", home);
        try
        {
            var result = GitWizardApi.PrettyPrintPath("%HOME%");
            Assert.That(result, Is.EqualTo(home));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }

    [TestCase("~/Documents")]
    [TestCase(@"~\Documents")]
    public void PrettyPrintPath_ExpandsTildePrefix(string input)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = GitWizardApi.PrettyPrintPath(input);
        Assert.That(result, Is.EqualTo(Path.Combine(profile, "Documents")));
    }

    [Test]
    public void PrettyPrintPath_PassesThroughStandaloneTilde()
    {
        var result = GitWizardApi.PrettyPrintPath("~");
        Assert.That(result, Is.EqualTo("~"));
    }

    [Test]
    public void PrettyPrintPath_PassesThroughPathWithoutEnvVars()
    {
        var path = "/some/fixed/path";
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
    public void PrettyPrintPath_ExpandsInMixedPath()
    {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Environment.SetEnvironmentVariable("HOME", home);
        try
        {
            var result = GitWizardApi.PrettyPrintPath("%HOME%/some/subdir");
            Assert.That(result, Is.EqualTo(home + "/some/subdir"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }

    [Test]
    public void PrettyPrintPath_DoesNotRequireDirectoryToExist()
    {
        var result = GitWizardApi.PrettyPrintPath("%HOME%");
        Assert.That(result, Is.Not.Null);
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
    public void ExpandSearchPath_TildePrefix_ExpandsToUserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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

    static readonly string[] BareAndNativeSeparatorTildeInputs = ["~", "~" + Path.DirectorySeparatorChar];

    [TestCaseSource(nameof(BareAndNativeSeparatorTildeInputs))]
    public void ExpandSearchPath_BareTilde_ExpandsToUserProfile(string input)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = GitWizardApi.ExpandSearchPath(input);
        Assert.That(result, Is.EqualTo(GitWizardApi.NormalizePath(profile)));
    }

    [Test]
    public void ExpandSearchPath_TildeUserName_IsNotTreatedAsHome()
    {
        var result = GitWizardApi.ExpandSearchPath("~git-wizard-no-such-user");
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ExpandIgnoredPath_ExpandsPath_WithoutRequiringDirectoryExistence()
    {
        var nonExistent = Path.Combine(_tempRoot!, "does-not-exist");
        var result = GitWizardApi.ExpandIgnoredPath(nonExistent);
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.EqualTo(nonExistent).IgnoreCase);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void ExpandIgnoredPath_ReturnsNull_ForNullOrWhiteSpace(string input)
    {
        Assert.That(GitWizardApi.ExpandIgnoredPath(input), Is.Null);
    }

    [Test]
    public void ExpandIgnoredPath_ExpandsEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("TEST_IGNORED_PATH", _tempRoot!);
        try
        {
            var result = GitWizardApi.ExpandIgnoredPath("%TEST_IGNORED_PATH%");
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.EqualTo(_tempRoot).IgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_IGNORED_PATH", null);
        }
    }

    [Test]
    public void ExpandIgnoredPaths_ExpandsCollectionAndFiltersNulls()
    {
        var nonExistentPath = Path.Combine(_tempRoot!, "another-non-existent-dir");
        var result = GitWizardApi.ExpandIgnoredPaths([nonExistentPath, "", "   "]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.Single(), Does.EndWith("another-non-existent-dir"));
    }
}
