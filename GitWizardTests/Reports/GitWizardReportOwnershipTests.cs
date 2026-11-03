using System.Diagnostics;
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

    [TestCase("--ref-format=reftable", "unsupported extension name extensions.refstorage", "reftable")]
    [TestCase("--object-format=sha256", "unknown object format 'sha256'", "sha256")]
    public void Refresh_UnsupportedFormatRepository_StaysVisibleWithPlainFormatErrorAndBypassesProbeMemory(
        string gitInitOption, string fallbackDiagnostic, string expectedFormatName)
    {
        var previousSilentMode = GitWizardLog.SilentMode;
        var previousTypedLogMethod = GitWizardLog.TypedLogMethod;
        var logMessages = new List<string?>();
        using var directory = new TempDirectory("gw-unsupported-format-");
        var initializedRealRepository = TryInitializeGitRepository(directory.Path, gitInitOption);
        var report = new GitWizardReport();
        var handler = new RecordingUpdateHandler
        {
            CreatingRepository = initializedRealRepository
                ? null
                : repository => repository.PhaseObserverForTesting =
                    _ => throw new LibGit2SharpException(fallbackDiagnostic),
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

            report.Refresh([directory.Path], handler);
            report.Refresh([directory.Path], handler);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(handler.RepositoriesCreated, Has.Length.EqualTo(2));
                Assert.That(report.NonRepositoryProbes, Is.Empty);
                Assert.That(report.NonRepositoryPaths, Is.Empty);
                Assert.That(report.Repositories.ContainsKey(directory.Path), Is.True);
                if (report.Repositories.TryGetValue(directory.Path, out var refreshedRepository))
                {
                    Assert.That(refreshedRepository.CurrentBranch, Is.Null);
                    Assert.That(refreshedRepository.HasPendingChanges, Is.False);
                    Assert.That(refreshedRepository.NumberOfPendingChanges, Is.Zero);
                    Assert.That(refreshedRepository.LocalOnlyCommits, Is.False);
                    Assert.That(refreshedRepository.Branches, Is.Null);
                    Assert.That(refreshedRepository.RecentCommits, Is.Null);
                    Assert.That(refreshedRepository.RefreshError,
                        Does.Contain("Unsupported repository format").IgnoreCase);
                    Assert.That(refreshedRepository.RefreshError,
                        Does.Contain(expectedFormatName).IgnoreCase);
                }

                Assert.That(logMessages, Has.None.Contains("Exception follows:"));
            }
        }
        finally
        {
            GitWizardLog.TypedLogMethod = previousTypedLogMethod;
            GitWizardLog.SilentMode = previousSilentMode;
        }
    }

    [TestCase("failed to read index", false)]
    [TestCase("access denied", false)]
    [TestCase("object not found", true)]
    public void Refresh_RepositoryFailure_StaysVisibleAndBypassesProbeMemory(string diagnostic, bool useNotFoundException)
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var report = new GitWizardReport();
        var handler = new RecordingUpdateHandler
        {
            CreatingRepository = repository => repository.PhaseObserverForTesting =
                _ => throw (useNotFoundException
                    ? new NotFoundException(diagnostic)
                    : new LibGit2SharpException(diagnostic)),
        };

        report.Refresh([fixture.Path], handler);
        report.Refresh([fixture.Path], handler);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.RepositoriesCreated, Has.Length.EqualTo(2));
            Assert.That(report.NonRepositoryProbes, Is.Empty);
            Assert.That(report.NonRepositoryPaths, Is.Empty);
            Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
            if (report.Repositories.TryGetValue(fixture.Path, out var refreshedRepository))
            {
                Assert.That(refreshedRepository.RefreshError, Does.Contain(diagnostic));
            }
        }
    }

    internal static bool TryInitializeGitRepository(string workingDirectory, string gitInitOption)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("init");
        startInfo.ArgumentList.Add(gitInitOption);
        TempRepoFixture.ScrubGitConfigOverrides(startInfo);

        using var process = Process.Start(startInfo);
        if (process is not null)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (process.WaitForExit(30000) && process.ExitCode == 0)
            {
                return true;
            }
        }

        TempDirectory.DeleteTree(workingDirectory);
        Directory.CreateDirectory(workingDirectory);
        Repository.Init(workingDirectory);
        return false;
    }
}
