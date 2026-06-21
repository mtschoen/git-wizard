using GitWizard;

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
        var directoryAsPath = Path.Combine(Path.GetTempPath(), "gw-cfg-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryAsPath);
        try
        {
            var configuration = new GitWizardConfiguration();
            Assert.DoesNotThrow(() => configuration.Save(directoryAsPath));
        }
        finally
        {
            Directory.Delete(directoryAsPath);
        }
    }

    [Test]
    public async Task SaveAsync_LogsAndSwallows_WhenPathIsADirectory()
    {
        var directoryAsPath = Path.Combine(Path.GetTempPath(), "gw-cfg-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryAsPath);
        try
        {
            var configuration = new GitWizardConfiguration();
            await configuration.SaveAsync(directoryAsPath);
            Assert.Pass("SaveAsync swallowed the directory-target write error.");
        }
        finally
        {
            Directory.Delete(directoryAsPath);
        }
    }

    [Test]
    public void GetRepositoryPaths_ScansConfiguredSearchPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-cfg-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));
        try
        {
            var configuration = new GitWizardConfiguration();
            configuration.SearchPaths.Add(root);
            var paths = new List<string>();

            configuration.GetRepositoryPaths(paths);

            var expected = Path.Combine(root, "repo");
            Assert.That(paths.Any(p => string.Equals(
                p.TrimEnd('/', '\\'), expected, StringComparison.OrdinalIgnoreCase)), Is.True,
                $"Expected the repo at {expected} to be discovered; got [{string.Join(", ", paths)}].");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
