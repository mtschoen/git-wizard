using System.Text.Json;
using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

[TestFixture]
[NonParallelizable]
public class SweepCliTests
{
    LocalFilesSandbox? _sandbox;
    string _temporaryHome = null!;

    [SetUp]
    public void SetUp()
    {
        _sandbox = new LocalFilesSandbox();
        _temporaryHome = _sandbox.Path;
    }

    [TearDown]
    public void TearDown()
    {
        _sandbox?.Dispose();
        _sandbox = null;
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
        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();

        var exitCode = await Program.RunCommandCoreAsync(runConfiguration, configuration,
            stdout: stdout, stderr: stderr);

        var report = JsonSerializer.Deserialize<RepositorySweepReport>(stdout.ToString());
        var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync();
        var expectedPaths = new[] { GitWizardApi.NormalizePath(newPath) };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero, stderr.ToString());
            Assert.That(report!.Repositories.Select(repository => repository.Path), Is.EqualTo(expectedPaths));
            Assert.That(cachedPaths, Is.EqualTo(expectedPaths));
        }
    }
}
