using System.Diagnostics;
using GitWizard.CLI;

namespace GitWizardTests.Watch;

[TestFixture]
[NonParallelizable]
public class ProgramWatchTests
{
    [Test]
    public async Task RunElevatedWatchViaSudoAsync_LaunchesSudoWithProcessPathAndArguments()
    {
        ProcessStartInfo? capturedStartInfo = null;

        var exitCode = await Program.RunElevatedWatchViaSudoAsync(
            "/custom/bin/git-wizard",
            "/custom/home/.GitWizard",
            ["--watch", "--config-path", "test.json"],
            (startInfo, _) =>
            {
                capturedStartInfo = startInfo;
                return Task.FromResult<int?>(37);
            },
            _ => { },
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(37));
            Assert.That(capturedStartInfo, Is.Not.Null);
            Assert.That(capturedStartInfo!.FileName, Is.EqualTo("sudo"));
            Assert.That(capturedStartInfo.UseShellExecute, Is.False);
            Assert.That(capturedStartInfo.ArgumentList, Is.EqualTo([
                "env",
                "GITWIZARD_HOME=/custom/home/.GitWizard",
                "/custom/bin/git-wizard",
                "--watch",
                "--config-path",
                "test.json"
            ]));
        }
    }

    [Test]
    public async Task RunElevatedWatchViaSudoAsync_MissingProcessPath_LogsErrorAndReturnsOne()
    {
        var loggedErrors = new List<string>();

        var exitCode = await Program.RunElevatedWatchViaSudoAsync(
            null,
            "/home/.GitWizard",
            ["--watch"],
            (_, _) => throw new AssertionException("A missing process path must not start a process."),
            loggedErrors.Add,
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(loggedErrors, Has.Count.EqualTo(1));
            Assert.That(loggedErrors[0], Does.Contain("Cannot determine the current executable path"));
        }
    }

    [Test]
    public async Task RunElevatedWatchViaSudoAsync_ProcessStartReturnsNull_LogsErrorAndReturnsOne()
    {
        var loggedErrors = new List<string>();

        var exitCode = await Program.RunElevatedWatchViaSudoAsync(
            "/usr/bin/git-wizard",
            "/home/.GitWizard",
            ["--watch"],
            (_, _) => Task.FromResult<int?>(null),
            loggedErrors.Add,
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(loggedErrors, Has.Count.EqualTo(1));
            Assert.That(loggedErrors[0], Does.Contain("Failed to start sudo process"));
        }
    }

    [Test]
    public async Task RunElevatedWatchViaSudoAsync_ProcessThrows_LogsErrorAndReturnsOne()
    {
        var loggedErrors = new List<string>();

        var exitCode = await Program.RunElevatedWatchViaSudoAsync(
            "/usr/bin/git-wizard",
            "/home/.GitWizard",
            ["--watch"],
            (_, _) => throw new InvalidOperationException("sudo failed to launch"),
            loggedErrors.Add,
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(loggedErrors, Has.Count.EqualTo(1));
            Assert.That(loggedErrors[0], Does.Contain("Could not start sudo: sudo failed to launch"));
        }
    }
}
