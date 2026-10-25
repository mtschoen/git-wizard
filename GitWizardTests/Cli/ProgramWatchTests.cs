using System.Diagnostics;
using GitWizard.CLI;

namespace GitWizardTests.Watch;

[TestFixture]
[NonParallelizable]
public class ProgramWatchTests
{
    [Test]
    public async Task RunElevatedWatchViaPkexecAsync_LaunchesPkexecWithProcessPathAndArguments()
    {
        ProcessStartInfo? capturedStartInfo = null;

        var exitCode = await Program.RunElevatedWatchViaPkexecAsync(
            "/custom/bin/git-wizard",
            "/custom/home/.GitWizard",
            ["--watch", "--config-path", "test.json"],
            startInfo =>
            {
                capturedStartInfo = startInfo;
                // Return a completed process (e.g. echo)
                return Process.Start(new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "true",
                    Arguments = OperatingSystem.IsWindows() ? "/c exit 0" : "",
                    UseShellExecute = false
                });
            },
            _ => { },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(capturedStartInfo, Is.Not.Null);
            Assert.That(capturedStartInfo!.FileName, Is.EqualTo("pkexec"));
            Assert.That(capturedStartInfo.UseShellExecute, Is.False);
            Assert.That(capturedStartInfo.ArgumentList, Is.EqualTo([
                "env",
                "GITWIZARD_HOME=/custom/home/.GitWizard",
                "/custom/bin/git-wizard",
                "--watch",
                "--config-path",
                "test.json"
            ]));
        });
    }

    [Test]
    public async Task RunElevatedWatchViaPkexecAsync_MissingProcessPath_LogsErrorAndReturnsOne()
    {
        var loggedErrors = new List<string>();

        var exitCode = await Program.RunElevatedWatchViaPkexecAsync(
            null,
            "/home/.GitWizard",
            ["--watch"],
            _ => throw new AssertionException("A missing process path must not start a process."),
            loggedErrors.Add,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(loggedErrors, Has.Count.EqualTo(1));
            Assert.That(loggedErrors[0], Does.Contain("Cannot determine the current executable path"));
        });
    }

    [Test]
    public async Task RunElevatedWatchViaPkexecAsync_ProcessStartReturnsNull_LogsErrorAndReturnsOne()
    {
        var loggedErrors = new List<string>();

        var exitCode = await Program.RunElevatedWatchViaPkexecAsync(
            "/usr/bin/git-wizard",
            "/home/.GitWizard",
            ["--watch"],
            _ => null,
            loggedErrors.Add,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(loggedErrors, Has.Count.EqualTo(1));
            Assert.That(loggedErrors[0], Does.Contain("Failed to start pkexec process"));
        });
    }

    [Test]
    public async Task RunElevatedWatchViaPkexecAsync_ProcessThrows_LogsErrorAndReturnsOne()
    {
        var loggedErrors = new List<string>();

        var exitCode = await Program.RunElevatedWatchViaPkexecAsync(
            "/usr/bin/git-wizard",
            "/home/.GitWizard",
            ["--watch"],
            _ => throw new InvalidOperationException("pkexec failed to launch"),
            loggedErrors.Add,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(loggedErrors, Has.Count.EqualTo(1));
            Assert.That(loggedErrors[0], Does.Contain("Could not start pkexec: pkexec failed to launch"));
        });
    }
}
