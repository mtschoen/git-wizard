using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// Covers <see cref="GitWizardConfiguration"/>'s save error handling (a write to an unwritable
/// path is logged, not thrown).
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
}
