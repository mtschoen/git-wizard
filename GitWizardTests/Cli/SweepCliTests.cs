using System.Text.Json;
using GitWizard;
using GitWizard.CLI;
using LibGit2Sharp;

namespace GitWizardTests;

[TestFixture]
[NonParallelizable]
public class SweepCliTests
{
    string _temporaryHome = null!;

    [SetUp]
    public void SetUp()
    {
        _temporaryHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();
    }

    [TearDown]
    public void TearDown()
    {
        TestUtilities.ResetStaticCaches();
        TestUtilities.ClearLocalFilesRedirect(_temporaryHome);
    }

    [Test]
    public async Task Sweep_RediscoversNewRepositoryAndReplacesStaleCachedList()
    {
        var searchPath = Path.Combine(_temporaryHome, "repositories");
        var removedPath = Path.Combine(searchPath, "removed");
        Directory.CreateDirectory(removedPath);
        await GitWizardApi.SaveCachedRepositoryPathsAsync([removedPath]);
        Directory.Delete(removedPath);

        var newPath = Path.Combine(searchPath, "new-repository");
        Repository.Init(newPath);
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { searchPath },
            IncludeTempDirectories = true
        };
        var runConfiguration = CliParser.Parse(["--sweep", "--no-mft"]);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunCommandCoreAsync(runConfiguration, configuration,
            stdout: stdout, stderr: stderr);

        var report = JsonSerializer.Deserialize<RepositorySweepReport>(stdout.ToString());
        var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync();
        var expectedPaths = new[] { GitWizardApi.NormalizePath(newPath) };
        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero, stderr.ToString());
            Assert.That(report!.Repositories.Select(repository => repository.Path), Is.EqualTo(expectedPaths));
            Assert.That(cachedPaths, Is.EqualTo(expectedPaths));
        });
    }
}
