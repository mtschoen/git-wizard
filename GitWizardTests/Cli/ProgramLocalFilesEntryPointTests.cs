using System.Runtime.Versioning;
using System.Text.Json;
using GitWizard;
using GitWizardTests.TestSupport;
using static GitWizardTests.CliProcess;

namespace GitWizardTests;

// Entry-point tests are separated by command concern because the combined fixture exceeds 500 lines.
public class ProgramLocalFilesEntryPointTests
{
    [Test]
    public async Task Main_ClearCacheFailureWithRebuildReport_KeepsDiagnosticOnStandardErrorAndStandardOutputIsJson()
    {
        using var repository = TempRepoFixture.CreateWithInitialCommit();
        using var homeOwner = new TempDirectory("gw-clear-cache-");
        var home = homeOwner.Path;
        var repositoryListPath = Path.Combine(home, "repositories.txt");
        await File.WriteAllTextAsync(repositoryListPath, repository.Path + Environment.NewLine);
        var restore = MakeUndeletable(repositoryListPath, home);
        try
        {
            var result = await RunCliInHomeAsync(
                home,
                "--clear-cache", "--no-mft", "--rebuild-report", "--no-local-commit-count", "--paths", repository.Path, "--json");

            using var document = JsonDocument.Parse(result.StandardOutput);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(document.RootElement.GetProperty("SchemaVersion").GetString(), Is.EqualTo("2.1"));
                Assert.That(result.StandardError, Does.Contain("Caught exception trying to clear cache"));
                Assert.That(result.StandardOutput, Does.Not.Contain("Caught exception"));
            }
        }
        finally
        {
            restore();
        }
    }


    // Makes File.Delete on the repository list fail: a read-only attribute on Windows, a
    // read-only containing directory elsewhere. Returns the action that undoes it.
    static Action MakeUndeletable(string filePath, string directoryPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return MakeDirectoryUndeletable(filePath, directoryPath);
        }

        File.SetAttributes(filePath, FileAttributes.ReadOnly);
        return () => File.SetAttributes(filePath, FileAttributes.Normal);
    }

    [UnsupportedOSPlatform("windows")]
    static Action MakeDirectoryUndeletable(string filePath, string directoryPath)
    {
        var originalMode = File.GetUnixFileMode(directoryPath);
        File.SetUnixFileMode(directoryPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            File.Delete(filePath);
            File.SetUnixFileMode(directoryPath, originalMode);
            Assert.Inconclusive("The runner can delete from a read-only directory.");
        }
        catch (UnauthorizedAccessException)
        {
        }

        return () => File.SetUnixFileMode(directoryPath, originalMode);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Main_DeleteAllRefusalIsNonzeroAndDoesNotContinue(bool alsoClear)
    {
        // Module initializers do not reach the child process; it inherits this owned home explicitly.
        using var sandbox = new LocalFilesSandbox();
        Assert.That(Environment.GetEnvironmentVariable("GITWIZARD_HOME"), Is.Not.Null.And.Not.Empty);
        var cache = MftIndexCachePolicy.GetCacheRoot(GitWizardApi.GetLocalFilesPath());
        Directory.CreateDirectory(cache);
        var sentinel = Path.Combine(cache, "sentinel.mlix");
        var list = GitWizardApi.GetCachedRepositoryListPath();
        await File.WriteAllTextAsync(sentinel, "retained");
        await File.WriteAllTextAsync(list, "repository sentinel");
        var arguments = new List<string> { "--delete-all-local-files", "--no-refresh", "--silent" };
        if (alsoClear)
        {
            arguments.Add("--clear-cache");
        }

        var result = await RunCliAsync([.. arguments]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ExitCode, Is.EqualTo(1));
            Assert.That(result.StandardError, Does.Contain("mft-index"));
            Assert.That(result.StandardOutput, Is.Empty);
            Assert.That(await File.ReadAllTextAsync(sentinel), Is.EqualTo("retained"));
            Assert.That(await File.ReadAllTextAsync(list), Is.EqualTo("repository sentinel"));
        }
    }
}
