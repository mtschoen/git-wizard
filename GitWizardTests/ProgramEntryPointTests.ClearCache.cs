using System.Runtime.Versioning;
using System.Text.Json;

namespace GitWizardTests;

public partial class ProgramEntryPointTests
{
    [Test]
    public async Task Main_ClearCacheFailureWithRebuildReport_KeepsDiagnosticOnStandardErrorAndStandardOutputIsJson()
    {
        using var repository = TempRepoFixture.CreateWithInitialCommit();
        var home = Path.Combine(Path.GetTempPath(), "gw-clear-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var repositoryListPath = Path.Combine(home, "repositories.txt");
        await File.WriteAllTextAsync(repositoryListPath, repository.Path + Environment.NewLine);
        var restore = MakeUndeletable(repositoryListPath, home);
        try
        {
            var result = await RunCliInHomeAsync(
                home,
                "--clear-cache", "--no-mft", "--rebuild-report", "--no-local-commit-count", "--paths", repository.Path, "--json");

            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.Multiple(() =>
            {
                Assert.That(document.RootElement.GetProperty("SchemaVersion").GetString(), Is.EqualTo("2.1"));
                Assert.That(result.StandardError, Does.Contain("Caught exception trying to clear cache"));
                Assert.That(result.StandardOutput, Does.Not.Contain("Caught exception"));
            });
        }
        finally
        {
            restore();
            Directory.Delete(home, recursive: true);
        }
    }

    // Makes File.Delete on the repository list fail: a read-only attribute on Windows, a
    // read-only containing directory elsewhere. Returns the action that undoes it.
    static Action MakeUndeletable(string filePath, string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(filePath, FileAttributes.ReadOnly);
            return () => File.SetAttributes(filePath, FileAttributes.Normal);
        }

        return MakeDirectoryUndeletable(filePath, directoryPath);
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
}
