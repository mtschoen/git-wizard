using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

[TestFixture]
public class InventoryWalkTests
{
    TempDirectory? _tempDirectory;
    string? _tempRoot;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = new TempDirectory("gw-inventory-");
        _tempRoot = _tempDirectory.Path;
        TestUtilities.ResetStaticCaches();
    }

    [TearDown]
    public void TearDown()
    {
        _tempDirectory?.Dispose();
    }

    [Test]
    public void ExpandAllClonePaths_IncludesRootRepoAndSubmoduleCheckout()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        var submoduleDir = repo.AddInitializedSubmodule("external/sub");

        var expanded = GitWizardApi.ExpandAllClonePaths([repo.Path]);

        var normalizedRepo = GitWizardApi.NormalizePath(repo.Path);
        var normalizedSub = GitWizardApi.NormalizePath(submoduleDir);

        Assert.That(expanded, Does.Contain(normalizedRepo));
        Assert.That(expanded, Does.Contain(normalizedSub));
    }

    [Test]
    public void ExpandAllClonePaths_IncludesLinkedWorktree()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        var worktreeDir = repo.AddWorktree("feature-worktree");

        var expanded = GitWizardApi.ExpandAllClonePaths([repo.Path]);

        var normalizedRepo = GitWizardApi.NormalizePath(repo.Path);
        var normalizedWt = GitWizardApi.NormalizePath(worktreeDir);

        Assert.That(expanded, Does.Contain(normalizedRepo));
        Assert.That(expanded, Does.Contain(normalizedWt));
    }

    [Test]
    public void ExpandAllClonePaths_DeduplicatesOverlappingPaths()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        var submoduleDir = repo.AddInitializedSubmodule("external/sub");

        // Pass both the parent repo and the submodule directly
        var expanded = GitWizardApi.ExpandAllClonePaths([repo.Path, submoduleDir, repo.Path]);

        var normalizedRepo = GitWizardApi.NormalizePath(repo.Path);
        var normalizedSub = GitWizardApi.NormalizePath(submoduleDir);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expanded.Count(p => string.Equals(p, normalizedRepo, StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));
            Assert.That(expanded.Count(p => string.Equals(p, normalizedSub, StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));
        }
    }

    [Test]
    public void ExpandAllClonePaths_IgnoresInvalidPaths()
    {
        var nonexistent = Path.Combine(_tempRoot!, "nonexistent");
        var expanded = GitWizardApi.ExpandAllClonePaths([nonexistent]);

        Assert.That(expanded, Is.Empty);
    }

    [Test]
    public void ExpandAllClonePaths_IgnoresExistingNonRepoDirectories()
    {
        var plainDir = Path.Combine(_tempRoot!, "plain-folder");
        Directory.CreateDirectory(plainDir);
        File.WriteAllText(Path.Combine(plainDir, "test.txt"), "hello");

        var expanded = GitWizardApi.ExpandAllClonePaths([plainDir]);

        Assert.That(expanded, Is.Empty);
    }

    [Test]
    public void ExpandAllClonePaths_MixedValidAndInvalidDirectories_ReturnsOnlyValidClones()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        var plainDir = Path.Combine(_tempRoot!, "plain-folder");
        Directory.CreateDirectory(plainDir);
        var nonexistent = Path.Combine(_tempRoot!, "does-not-exist");

        var expanded = GitWizardApi.ExpandAllClonePaths([plainDir, repo.Path, nonexistent]);

        var normalizedRepo = GitWizardApi.NormalizePath(repo.Path);
        Assert.That(expanded, Has.Count.EqualTo(1));
        Assert.That(expanded[0], Is.EqualTo(normalizedRepo));
    }

    [Test]
    public async Task DiscoverAllCloneInventoryAsync_FindsOwnedCrewClones()
    {
        // Set up a mock structure simulating ~/.pr-crew/repos and normal search paths
        var crewReposDir = Path.Combine(_tempRoot!, "crew_repos");
        Directory.CreateDirectory(crewReposDir);

        var crewRepoPath = Path.Combine(crewReposDir, "schoen_test_repo");
        Directory.CreateDirectory(crewRepoPath);
        Repository.Init(crewRepoPath);
        using (var repo = new Repository(crewRepoPath))
        {
            var readme = Path.Combine(crewRepoPath, "README.md");
            await File.WriteAllTextAsync(readme, "crew repo").ConfigureAwait(false);
            Commands.Stage(repo, "README.md");
            var sig = new Signature("Test", "test@example.com", DateTimeOffset.Now);
            repo.Commit("initial", sig, sig);
        }

        // Also create a bare-ish clone in crewReposDir
        var bareRepoPath = Path.Combine(crewReposDir, "schoen_bare.git");
        Directory.CreateDirectory(bareRepoPath);
        Repository.Init(bareRepoPath, isBare: true);

        var config = new GitWizardConfiguration();
        var coordinator = new RepositoryDiscoveryCoordinator(RepositoryDiscoveryDependencies.CreateDefault(
            additionalCloneRoots: () => [crewReposDir]));
        await using var inventory = await GitWizardApi.DiscoverAllCloneInventoryAsync(config, null, true, coordinator);
        var allClones = inventory.Inventory.Paths;

        var normalizedCrewRepo = GitWizardApi.NormalizePath(crewRepoPath);
        var normalizedBareRepo = GitWizardApi.NormalizePath(bareRepoPath);

        Assert.That(allClones, Does.Contain(normalizedCrewRepo));
        Assert.That(allClones, Does.Contain(normalizedBareRepo));
    }

    [Test]
    public async Task DiscoverAllClonePathsAsync_BareRepoInIgnoredPath_IsExcluded()
    {
        var scanDir = Path.Combine(_tempRoot!, "scan_root");
        Directory.CreateDirectory(scanDir);

        var ignoredDir = Path.Combine(scanDir, "ignored_folder");
        Directory.CreateDirectory(ignoredDir);

        var bareRepoPath = Path.Combine(ignoredDir, "bare_repo.git");
        Directory.CreateDirectory(bareRepoPath);
        Repository.Init(bareRepoPath, isBare: true);

        var config = new GitWizardConfiguration
        {
            SearchPaths = { scanDir },
            IgnoredPaths = { ignoredDir }
        };

        var coordinator = new RepositoryDiscoveryCoordinator(RepositoryDiscoveryDependencies.CreateDefault(
            additionalCloneRoots: () => []));
        await using var inventory = await GitWizardApi.DiscoverAllCloneInventoryAsync(config, null, true, coordinator);
        var allClones = inventory.Inventory.Paths;
        var normalizedBareRepo = GitWizardApi.NormalizePath(bareRepoPath);

        Assert.That(allClones, Does.Not.Contain(normalizedBareRepo));
    }

    [Test]
    public void GetPrCrewReposPath_ReturnsStandardPath()
    {
        var path = GitWizardApi.GetPrCrewReposPath();
        Assert.That(path, Does.EndWith(Path.Combine(".pr-crew", "repos")));
    }
}
