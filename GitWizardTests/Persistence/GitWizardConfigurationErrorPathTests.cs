using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// Covers <see cref="GitWizardConfiguration"/>'s save error handling (a write to an unwritable
/// path is logged, not thrown) and the instance <see cref="GitWizardConfiguration.GetRepositoryPaths"/>
/// scan over the configured search paths.
/// </summary>
public class GitWizardConfigurationErrorPathTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void Save_LogsAndSwallows_WhenPathIsADirectory()
    {
        using var directory = new TempDirectory("gw-cfg-dir-");
        var directoryAsPath = directory.Path;
        var configuration = new GitWizardConfiguration();
        configuration.Save(directoryAsPath);
    }

    [Test]
    public async Task SaveAsync_LogsAndSwallows_WhenPathIsADirectory()
    {
        using var directory = new TempDirectory("gw-cfg-dir-");
        var directoryAsPath = directory.Path;
        var configuration = new GitWizardConfiguration();
        await configuration.SaveAsync(directoryAsPath);
        Assert.Pass("SaveAsync swallowed the directory-target write error.");
    }

    [Test]
    public void GetRepositoryPaths_ScansConfiguredSearchPaths()
    {
        using var directory = new TempDirectory("gw-cfg-scan-");
        var root = directory.Path;
        Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));

        var configuration = new GitWizardConfiguration();
        configuration.SearchPaths.Add(root);
        var paths = new List<string>();

        configuration.GetRepositoryPaths(paths);

        var expected = Path.Combine(root, "repo");
        Assert.That(paths.Any(p => string.Equals(
            p.TrimEnd('/', '\\'), expected, StringComparison.OrdinalIgnoreCase)), Is.True,
            $"Expected the repo at {expected} to be discovered; got [{string.Join(", ", paths)}].");
    }
}
