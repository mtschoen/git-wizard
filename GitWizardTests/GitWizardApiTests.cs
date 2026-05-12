using GitWizard;

namespace GitWizardTests;

public class GitWizardApiTests
{
    string? _tempRoot;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _tempRoot = Path.Combine(Path.GetTempPath(), "GitWizardApiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (!string.IsNullOrEmpty(_tempRoot) && Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public void GetRepositoryPaths_IgnoresExpandedIgnoredPathDescendants()
    {
        var root = Path.Combine(_tempRoot!, "root");
        var ignored = Path.Combine(root, "ignored");
        var included = Path.Combine(root, "included");

        Directory.CreateDirectory(Path.Combine(ignored, "repo", ".git"));
        Directory.CreateDirectory(Path.Combine(included, "repo", ".git"));

        Environment.SetEnvironmentVariable("GITWIZARD_IGNORED", ignored);
        try
        {
            var paths = new SortedSet<string>();
            GitWizardApi.GetRepositoryPaths(root, paths, new[] { "%GITWIZARD_IGNORED%" });

            Assert.That(paths, Has.Count.EqualTo(1));
            Assert.That(paths.Single(), Does.EndWith(Path.Combine("included", "repo")).IgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITWIZARD_IGNORED", null);
        }
    }

    [Test]
    public void PrettyPrintPath_ExpandsEnvironmentVariables()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Environment.SetEnvironmentVariable("HOME", home);
        try
        {
            var result = GitWizardApi.PrettyPrintPath("%HOME%");
            Assert.That(result, Is.EqualTo(home));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", null);
        }
    }

    [Test]
    public void PrettyPrintPath_ExpandsTildePrefix()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = GitWizardApi.PrettyPrintPath("~/Documents");
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
    public void PrettyPrintPath_ExpandsInMixedPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Environment.SetEnvironmentVariable("HOME", home);
        try
        {
            var result = GitWizardApi.PrettyPrintPath("%HOME%/some/subdir");
            Assert.That(result, Is.EqualTo(home + "/some/subdir"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", null);
        }
    }

    [Test]
    public void PrettyPrintPath_DoesNotRequireDirectoryToExist()
    {
        var result = GitWizardApi.PrettyPrintPath("%HOME%");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void GetRepositoryPaths_DoesNotTreatSharedPrefixAsIgnoredParent()
    {
        var root = Path.Combine(_tempRoot!, "root");
        var ignored = Path.Combine(root, "foo");
        var sibling = Path.Combine(root, "foobar");

        Directory.CreateDirectory(Path.Combine(ignored, "repo", ".git"));
        Directory.CreateDirectory(Path.Combine(sibling, "repo", ".git"));

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(root, paths, new[] { ignored });

        Assert.That(paths, Has.Count.EqualTo(1));
        Assert.That(paths.Single(), Does.EndWith(Path.Combine("foobar", "repo")).IgnoreCase);
    }
}
