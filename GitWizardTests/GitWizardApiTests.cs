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
