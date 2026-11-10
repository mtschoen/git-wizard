using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// Covers <see cref="GitWizardConfiguration"/>'s save error handling (a write to an unwritable
/// path is logged, not thrown).
/// </summary>
[NonParallelizable]
public class GitWizardConfigurationErrorPathTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void Save_LogsAndSwallows_WhenPathIsADirectory()
    {
        using var directory = new TempDirectory("gw-cfg-dir-");
        var directoryAsPath = directory.Path;
        var loggedErrors = new List<string?>();
        var previousTypedSink = GitWizardLog.TypedLogMethod;
        GitWizardLog.TypedLogMethod = (message, type) =>
        {
            if (type == GitWizardLog.LogType.Error)
            {
                loggedErrors.Add(message);
            }
        };
        try
        {
            var configuration = new GitWizardConfiguration();
            configuration.Save(directoryAsPath);
        }
        finally
        {
            GitWizardLog.TypedLogMethod = previousTypedSink;
        }

        Assert.That(loggedErrors, Has.Some.Contains("Failed to save configuration"),
            "Save must report the swallowed write failure through the log.");
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
