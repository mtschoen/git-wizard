using GitWizard;

namespace GitWizardTests;

/// <summary>
/// The process-exit flush must write every queued record, including the last ones, to the log
/// file before the process ends (git-wizard#454 W7). NonParallelizable because the file sink is
/// global static state.
/// </summary>
[NonParallelizable]
// Log flush tests exercise draining queued file records at exit.
public class GitWizardLogFlushTests
{
    const int QueuedRecordCount = 800;

    Action<string?> _originalLogMethod = Console.WriteLine;
    bool _originalSilentMode;

    [SetUp]
    public void SetUp()
    {
        _originalLogMethod = GitWizardLog.LogMethod;
        _originalSilentMode = GitWizardLog.SilentMode;
        GitWizardLog.CloseCurrentLogFile();
        GitWizardLog.SilentMode = false;
        GitWizardLog.LogMethod = _ => { };
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.CloseCurrentLogFile();
        GitWizardLog.SilentMode = _originalSilentMode;
        GitWizardLog.LogMethod = _originalLogMethod;
    }

    [Test]
    public async Task FlushForProcessExit_WritesEveryQueuedRecord()
    {
        var token = Guid.NewGuid().ToString("N");
        for (var index = 0; index < QueuedRecordCount; index++)
        {
            GitWizardLog.Log($"flush-{token}-{index}");
        }

        GitWizardLog.FlushForProcessExit();

        var files = Directory.GetFiles(GitWizardApi.GetLogFolderPath(), "GitWizardLog_*.log");
        var contents = string.Join(Environment.NewLine,
            await Task.WhenAll(files.Select(path => File.ReadAllTextAsync(path))));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(contents, Does.Contain($"flush-{token}-0"));
            Assert.That(contents, Does.Contain($"flush-{token}-{QueuedRecordCount - 1}"));
        }
    }

    [Test]
    public async Task Log_AfterFlush_StillWritesToTheLogFile()
    {
        GitWizardLog.FlushForProcessExit();
        var marker = $"after-flush-{Guid.NewGuid():N}";

        GitWizardLog.Log(marker);
        GitWizardLog.CloseCurrentLogFile();

        var files = Directory.GetFiles(GitWizardApi.GetLogFolderPath(), "GitWizardLog_*.log");
        var contents = string.Join(Environment.NewLine,
            await Task.WhenAll(files.Select(path => File.ReadAllTextAsync(path))));
        Assert.That(contents, Does.Contain(marker));
    }
}
