using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

[NonParallelizable]
public class GitWizardReportOwnershipTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void Refresh_OwnershipBlockedRepository_BypassesProbeMemory(bool caughtInsidePhase)
    {
        var previousSilentMode = GitWizardLog.SilentMode;
        var previousTypedLogMethod = GitWizardLog.TypedLogMethod;
        var logMessages = new List<string?>();
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var fixturePath = fixture.Path;
        var report = new GitWizardReport();
        var handler = new RecordingUpdateHandler
        {
            CreatingRepository = repository => repository.PhaseObserverForTesting = phaseIndex =>
            {
                if (phaseIndex != 0)
                {
                    return;
                }

                var exception = new LibGit2SharpException(
                    $"repository path '{fixturePath}' is not owned by current user");
                if (caughtInsidePhase)
                {
                    repository.LogRepositoryException(exception, "ownership probe");
                }
                else
                {
                    throw exception;
                }
            },
        };

        try
        {
            GitWizardLog.SilentMode = true;
            GitWizardLog.TypedLogMethod = (message, _) =>
            {
                lock (logMessages)
                {
                    logMessages.Add(message);
                }
            };

            report.Refresh([fixture.Path], handler);
            report.Refresh([fixture.Path], handler);

            var ownershipLines = logMessages.Where(message =>
                message?.Contains("ownership-blocked: needs safe.directory", StringComparison.Ordinal) == true).ToArray();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(handler.RepositoriesCreated, Has.Length.EqualTo(2),
                    "Ownership-blocked repositories must be probed on every refresh.");
                Assert.That(report.NonRepositoryProbes, Is.Empty);
                Assert.That(report.NonRepositoryPaths, Is.Empty);
                Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
                Assert.That(report.Repositories[fixture.Path].RefreshError,
                    Is.EqualTo("ownership-blocked: needs safe.directory"));
                Assert.That(ownershipLines, Has.Length.EqualTo(2));
                Assert.That(ownershipLines, Has.All.Not.Contain(Environment.NewLine));
                Assert.That(logMessages, Has.None.Contains("Exception follows:"));
                Assert.That(logMessages, Has.None.Contains("LibGit2Sharp.LibGit2SharpException:"));
            }
        }
        finally
        {
            GitWizardLog.TypedLogMethod = previousTypedLogMethod;
            GitWizardLog.SilentMode = previousSilentMode;
        }
    }

    [TestCase("unsupported extension name extensions.refstorage", true)]
    [TestCase("unknown object format 'sha256'", true)]
    [TestCase("failed to read index", true)]
    [TestCase("access denied", false)]
    public void Refresh_LibGitFailure_CachesOnlyRecognizedJunk(string diagnostic, bool shouldCache)
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var report = new GitWizardReport();
        var handler = new RecordingUpdateHandler
        {
            CreatingRepository = repository => repository.PhaseObserverForTesting =
                _ => throw new LibGit2SharpException(diagnostic),
        };

        report.Refresh([fixture.Path], handler);
        report.Refresh([fixture.Path], handler);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.RepositoriesCreated, Has.Length.EqualTo(shouldCache ? 1 : 2));
            Assert.That(report.NonRepositoryProbes, shouldCache ? Has.Count.EqualTo(1) : Is.Empty);
            Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.EqualTo(!shouldCache));
        }
    }
}
