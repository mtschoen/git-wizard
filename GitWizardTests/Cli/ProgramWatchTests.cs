using System.Diagnostics;
using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;

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

    [Test]
    public async Task RunUnelevatedWatchAsync_NoUsableSearchPathsAndNoRetainedRoots_ReportsErrorsWithoutLaunchingSudo()
    {
        var configuration = new GitWizardConfiguration { SearchPaths = { "/nonexistent/search/one", "/nonexistent/search/two" } };
        using var stderr = new StringWriter();

        var exitCode = await Program.RunUnelevatedWatchAsync(
            new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false, []),
            stderr,
            (_, _) => throw new AssertionException("An unusable configuration must not launch sudo."),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stderr.ToString(), Does.Contain("Discovery error (/nonexistent/search/one): Search path does not exist"));
            Assert.That(stderr.ToString(), Does.Contain("Discovery error (/nonexistent/search/two): Search path does not exist"));
            Assert.That(stderr.ToString(), Does.Contain("Discovery error (*): No search roots configured."));
        }
    }

    [TestCase("denied-stat", 23)]
    [TestCase("denied-enumeration", 23)]
    [TestCase("unknown-io", 23)]
    [TestCase("present-entry", 23)]
    [TestCase("absent", 1)]
    [TestCase("file", 1)]
    public async Task RunUnelevatedWatchAsync_IndeterminateRootIssue614_PreservesElevation(string observation, int expectedExitCode)
    {
        using var parent = new TempDirectory("WatchPermissionRoot-");
        var searchPath = Path.Combine(parent.Path, "private", "search");
        var privatePath = Path.GetDirectoryName(searchPath)!;
        var configuration = new GitWizardConfiguration { SearchPaths = { searchPath } };
        ProcessStartInfo? capturedStartInfo = null;
        using var stderr = new StringWriter();

        FileAttributes GetAttributes(string path)
        {
            if (path == privatePath)
            {
                return FileAttributes.Directory;
            }

            Assert.That(path, Is.EqualTo(searchPath));
            return observation switch
            {
                "denied-stat" => throw new UnauthorizedAccessException(),
                "unknown-io" => throw new IOException("Cannot determine root existence."),
                "file" => FileAttributes.Normal,
                _ => throw new DirectoryNotFoundException()
            };
        }

        IEnumerable<string> EnumerateEntries(string path)
        {
            Assert.That(path, Is.EqualTo(privatePath));
            return observation switch
            {
                "denied-enumeration" => throw new UnauthorizedAccessException(),
                "present-entry" => [searchPath],
                _ => []
            };
        }

        var exitCode = await Program.RunUnelevatedWatchAsync(
            new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false, []), stderr,
            (startInfo, _) =>
            {
                capturedStartInfo = startInfo;
                return Task.FromResult<int?>(23);
            },
            CancellationToken.None, GetAttributes, EnumerateEntries);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(expectedExitCode));
            Assert.That(capturedStartInfo?.FileName, expectedExitCode == 23 ? Is.EqualTo("sudo") : Is.Null);
            Assert.That(stderr.ToString(), Is.EqualTo(expectedExitCode == 23 ? string.Empty :
                $"Discovery error ({searchPath}): Search path does not exist or could not be expanded: {searchPath}{Environment.NewLine}" +
                $"Discovery error (*): No search roots configured.{Environment.NewLine}"));
        }
    }

    [Test]
    public async Task RunUnelevatedWatchAsync_NoSearchPathsConfigured_ReportsErrorWithoutLaunchingSudo()
    {
        using var stderr = new StringWriter();

        var exitCode = await Program.RunUnelevatedWatchAsync(
            new DiscoveryRequest(new GitWizardConfiguration(), DiscoveryPurpose.Watch, false, false, []),
            stderr,
            (_, _) => throw new AssertionException("An unusable configuration must not launch sudo."),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stderr.ToString(), Does.Contain("Discovery error (*): No search roots configured."));
        }
    }

    [Test]
    public async Task RunUnelevatedWatchAsync_UsableSearchPath_LaunchesSudo()
    {
        using var searchRoot = new TempDirectory("WatchSearchRoot-");
        var configuration = new GitWizardConfiguration { SearchPaths = { searchRoot.Path, "/nonexistent/search/one" } };
        ProcessStartInfo? capturedStartInfo = null;
        using var stderr = new StringWriter();

        var exitCode = await Program.RunUnelevatedWatchAsync(
            new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false, []),
            stderr,
            (startInfo, _) =>
            {
                capturedStartInfo = startInfo;
                return Task.FromResult<int?>(0);
            },
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(capturedStartInfo?.FileName, Is.EqualTo("sudo"));
            Assert.That(stderr.ToString(), Is.Empty);
        }
    }

    [Test]
    public async Task RunUnelevatedWatchAsync_NoUsableSearchPathsButRetainedRoot_LaunchesSudo()
    {
        using var retainedRoot = new TempDirectory("WatchRetainedRoot-");
        var configuration = new GitWizardConfiguration { SearchPaths = { "/nonexistent/search/one" } };
        ProcessStartInfo? capturedStartInfo = null;
        using var stderr = new StringWriter();

        var exitCode = await Program.RunUnelevatedWatchAsync(
            new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false, [retainedRoot.Path]),
            stderr,
            (startInfo, _) =>
            {
                capturedStartInfo = startInfo;
                return Task.FromResult<int?>(0);
            },
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(capturedStartInfo?.FileName, Is.EqualTo("sudo"));
            Assert.That(stderr.ToString(), Is.Empty);
        }
    }
}
