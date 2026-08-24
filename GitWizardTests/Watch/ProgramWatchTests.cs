using System.Diagnostics;
using GitWizard.CLI;

namespace GitWizardTests.Watch;

[TestFixture]
public class ProgramWatchTests
{
    [TearDown]
    public void TearDown()
    {
        Program.ResetWatchDependenciesToDefault();
    }

    [Test]
    public async Task RunElevatedWatchViaPkexecAsync_LaunchesPkexecWithProcessPathAndArguments()
    {
        ProcessStartInfo? capturedStartInfo = null;
        Program._getProcessPath = () => "/custom/bin/git-wizard";
        Program._getLocalFilesPath = () => "/custom/home/.GitWizard";
        Program._startProcess = startInfo =>
        {
            capturedStartInfo = startInfo;
            // Return a completed process (e.g. echo)
            return Process.Start(new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "true",
                Arguments = OperatingSystem.IsWindows() ? "/c exit 0" : "",
                UseShellExecute = false
            });
        };

        var exitCode = await Program.RunElevatedWatchViaPkexecAsync(
            new[] { "--watch", "--config-path", "test.json" });

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(capturedStartInfo, Is.Not.Null);
            Assert.That(capturedStartInfo!.FileName, Is.EqualTo("pkexec"));
            Assert.That(capturedStartInfo.UseShellExecute, Is.False);
            Assert.That(capturedStartInfo.ArgumentList, Is.EqualTo(new[]
            {
                "env",
                "GITWIZARD_HOME=/custom/home/.GitWizard",
                "/custom/bin/git-wizard",
                "--watch",
                "--config-path",
                "test.json"
            }));
        });
    }

    [Test]
    public async Task RunElevatedWatchViaPkexecAsync_MissingProcessPath_LogsErrorAndReturnsOne()
    {
        var loggedErrors = new List<string>();
        Program._getProcessPath = () => null;

        var exitCode = await Program.RunElevatedWatchViaPkexecAsync(
            new[] { "--watch" },
            logError: loggedErrors.Add);

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
        Program._getProcessPath = () => "/usr/bin/git-wizard";
        Program._startProcess = _ => null;

        var exitCode = await Program.RunElevatedWatchViaPkexecAsync(
            new[] { "--watch" },
            logError: loggedErrors.Add);

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
        Program._getProcessPath = () => "/usr/bin/git-wizard";
        Program._startProcess = _ => throw new InvalidOperationException("pkexec failed to launch");

        var exitCode = await Program.RunElevatedWatchViaPkexecAsync(
            new[] { "--watch" },
            logError: loggedErrors.Add);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(loggedErrors, Has.Count.EqualTo(1));
            Assert.That(loggedErrors[0], Does.Contain("Could not start pkexec: pkexec failed to launch"));
        });
    }
}
