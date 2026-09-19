using GitWizard;
using GitWizard.CLI;
using LibGit2Sharp;

namespace GitWizardTests;

[TestFixture]
[NonParallelizable]
public class ProgramWatchStartupTests
{
    string _home = null!;
    string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _home = TestUtilities.RedirectLocalFilesToTemp();
        _root = Path.Combine(_home, "search");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => TestUtilities.ClearLocalFilesRedirect(_home);

    [TestCase(false)]
    [TestCase(true)]
    public async Task LoadTrackedRepositoryPathsAsync_DiscoversRepositoryCreatedAfterCacheWrite(
        bool seedCache)
    {
        var existing = CreateRepository("existing");
        if (seedCache)
            await GitWizardApi.SaveCachedRepositoryPathsAsync([existing]);

        var fresh = CreateRepository("fresh");
        var paths = await Program.LoadTrackedRepositoryPathsAsync(CreateConfiguration());
        var cached = await GitWizardApi.GetCachedRepositoryPathsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(paths, Is.EquivalentTo(new[] { existing, fresh }));
            Assert.That(cached, Is.EquivalentTo(new[] { existing, fresh }));
        });
    }

    [Test]
    public async Task LoadTrackedRepositoryPathsAsync_ReplacesCachedPathsUsingCurrentScope()
    {
        var ignored = CreateRepository("ignored");
        var outsideDirectory = Path.Combine(_home, "outside");
        Repository.Init(outsideDirectory);
        var outside = GitWizardApi.NormalizePath(outsideDirectory);
        var missing = GitWizardApi.NormalizePath(Path.Combine(_root, "missing"));
        await GitWizardApi.SaveCachedRepositoryPathsAsync([ignored, outside, missing]);
        var fresh = CreateRepository("fresh");
        var configuration = CreateConfiguration();
        configuration.IgnoredPaths.Add(Path.Combine(_root, "ignored"));

        var paths = await Program.LoadTrackedRepositoryPathsAsync(configuration);
        var cached = await GitWizardApi.GetCachedRepositoryPathsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(paths, Is.EquivalentTo(new[] { fresh }));
            Assert.That(cached, Is.EquivalentTo(new[] { fresh }));
        });
    }

    [Test]
    public async Task LoadTrackedRepositoryPathsAsync_EmptyDiscoveryClearsStaleCache()
    {
        var missing = GitWizardApi.NormalizePath(Path.Combine(_root, "missing"));
        await GitWizardApi.SaveCachedRepositoryPathsAsync([missing]);

        var paths = await Program.LoadTrackedRepositoryPathsAsync(CreateConfiguration());
        var cached = await GitWizardApi.GetCachedRepositoryPathsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(paths, Is.Empty);
            Assert.That(cached, Is.Null);
        });
    }

    GitWizardConfiguration CreateConfiguration() => new()
    {
        SearchPaths = { _root },
        IncludeTempDirectories = true
    };

    string CreateRepository(string name)
    {
        var path = Path.Combine(_root, name);
        Repository.Init(path);
        return GitWizardApi.NormalizePath(path);
    }
}
