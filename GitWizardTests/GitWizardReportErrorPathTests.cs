using GitWizard;

namespace GitWizardTests;

/// <summary>
/// Covers <see cref="GitWizardReport"/>'s defensive error paths: a refresh whose update handler
/// throws from every callback (each must be swallowed so one bad handler can't abort a scan), and
/// Save/SaveAsync against an unwritable path (the write exception must be logged, not thrown).
/// </summary>
public class GitWizardReportErrorPathTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void Refresh_SwallowsThrowingHandlerCallbacks()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var report = new GitWizardReport();
        var handler = new ThrowingProgressHandler();

        // Every handler callback throws; Refresh must complete and still record the repository.
        Assert.DoesNotThrow(() => report.Refresh(new[] { fixture.Path }, handler));

        Assert.Multiple(() =>
        {
            Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True,
                "The repository must be scanned even though every handler callback threw.");
            Assert.That(handler.StartProgressCalls, Is.GreaterThan(0));
            Assert.That(handler.RepositoryCreatedCalls, Is.GreaterThan(0));
            Assert.That(handler.UpdateProgressCalls, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Save_LogsAndSwallows_WhenPathIsADirectory()
    {
        var directoryAsPath = Path.Combine(Path.GetTempPath(), "gw-save-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryAsPath);
        try
        {
            var report = new GitWizardReport();

            // Writing a file to a path that is an existing directory throws; Save must swallow it.
            Assert.DoesNotThrow(() => report.Save(directoryAsPath));
        }
        finally
        {
            Directory.Delete(directoryAsPath);
        }
    }

    [Test]
    public async Task SaveAsync_LogsAndSwallows_WhenPathIsADirectory()
    {
        var directoryAsPath = Path.Combine(Path.GetTempPath(), "gw-save-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryAsPath);
        try
        {
            var report = new GitWizardReport();

            await report.SaveAsync(directoryAsPath);
            Assert.Pass("SaveAsync swallowed the directory-target write error.");
        }
        finally
        {
            Directory.Delete(directoryAsPath);
        }
    }

    sealed class ThrowingProgressHandler : IUpdateHandler
    {
        public int StartProgressCalls { get; private set; }
        public int RepositoryCreatedCalls { get; private set; }
        public int UpdateProgressCalls { get; private set; }

        public void StartProgress(string description, int total)
        {
            StartProgressCalls++;
            throw new InvalidOperationException("start-progress failure");
        }

        public void UpdateProgress(int count)
        {
            UpdateProgressCalls++;
            throw new InvalidOperationException("update-progress failure");
        }

        public void SendUpdateMessage(string? message) { }

        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository)
        {
            RepositoryCreatedCalls++;
            throw new InvalidOperationException("repository-created failure");
        }

        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    }
}
