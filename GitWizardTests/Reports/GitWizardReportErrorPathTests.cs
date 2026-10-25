using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// Covers <see cref="GitWizardReport"/>'s defensive error paths: a refresh whose update handler
/// throws from every callback (each must be swallowed so one bad handler can't abort a scan).
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
        var startProgressCalls = 0;
        var repositoryCreatedCalls = 0;
        var updateProgressCalls = 0;
        var handler = new RecordingUpdateHandler
        {
            StartingProgress = (_, _) =>
            {
                startProgressCalls++;
                throw new InvalidOperationException("start-progress failure");
            },
            CreatingRepository = _ =>
            {
                repositoryCreatedCalls++;
                throw new InvalidOperationException("repository-created failure");
            },
            UpdatingProgress = _ =>
            {
                updateProgressCalls++;
                throw new InvalidOperationException("update-progress failure");
            }
        };

        // Every handler callback throws; Refresh must complete and still record the repository.
        report.Refresh([fixture.Path], handler);

        Assert.Multiple(() =>
        {
            Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True,
                "The repository must be scanned even though every handler callback threw.");
            Assert.That(startProgressCalls, Is.GreaterThan(0));
            Assert.That(repositoryCreatedCalls, Is.GreaterThan(0));
            Assert.That(updateProgressCalls, Is.GreaterThan(0));
        });
    }
}
