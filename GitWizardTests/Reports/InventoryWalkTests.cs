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

        var normalizedRepo = GitWizardApi.ResolveOnDiskPath(repo.Path);
        var normalizedSub = GitWizardApi.ResolveOnDiskPath(submoduleDir);

        Assert.That(expanded, Does.Contain(normalizedRepo));
        Assert.That(expanded, Does.Contain(normalizedSub));
    }

    [Test]
    public void ExpandAllClonePaths_IncludesLinkedWorktree()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        var worktreeDir = repo.AddWorktree("feature-worktree");

        var expanded = GitWizardApi.ExpandAllClonePaths([repo.Path]);

        var normalizedRepo = GitWizardApi.ResolveOnDiskPath(repo.Path);
        var normalizedWt = GitWizardApi.ResolveOnDiskPath(worktreeDir);

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

        var normalizedRepo = GitWizardApi.ResolveOnDiskPath(repo.Path);
        var normalizedSub = GitWizardApi.ResolveOnDiskPath(submoduleDir);

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

        var normalizedRepo = GitWizardApi.ResolveOnDiskPath(repo.Path);
        Assert.That(expanded, Has.Count.EqualTo(1));
        Assert.That(expanded[0], Is.EqualTo(normalizedRepo));
    }

    [Test]
    public async Task DiscoverAllCloneInventoryAsync_FindsClonesUnderAConfiguredSearchPath()
    {
        var cloneRoot = Path.Combine(_tempRoot!, "clone_root");
        Directory.CreateDirectory(cloneRoot);

        var firstClonePath = Path.Combine(cloneRoot, "test_repo");
        Directory.CreateDirectory(firstClonePath);
        Repository.Init(firstClonePath);
        using (var repo = new Repository(firstClonePath))
        {
            var readme = Path.Combine(firstClonePath, "README.md");
            await File.WriteAllTextAsync(readme, "first clone").ConfigureAwait(false);
            Commands.Stage(repo, "README.md");
            var sig = new Signature("Test", "test@example.com", DateTimeOffset.Now);
            repo.Commit("initial", sig, sig);
        }

        // Also create a bare-ish clone in cloneRoot
        var bareRepoPath = Path.Combine(cloneRoot, "bare_repo.git");
        Directory.CreateDirectory(bareRepoPath);
        Repository.Init(bareRepoPath, isBare: true);

        var config = new GitWizardConfiguration { SearchPaths = { cloneRoot } };
        var coordinator = new RepositoryDiscoveryCoordinator(RepositoryDiscoveryDependencies.CreateDefault());
        await using var inventory = await GitWizardApi.DiscoverAllCloneInventoryAsync(config, null, true, coordinator);
        var allClones = inventory.Inventory.Paths;

        var normalizedFirstClone = GitWizardApi.ResolveOnDiskPath(firstClonePath);
        var normalizedBareRepo = GitWizardApi.ResolveOnDiskPath(bareRepoPath);

        Assert.That(allClones, Does.Contain(normalizedFirstClone));
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

        var coordinator = new RepositoryDiscoveryCoordinator(RepositoryDiscoveryDependencies.CreateDefault());
        await using var inventory = await GitWizardApi.DiscoverAllCloneInventoryAsync(config, null, true, coordinator);
        var allClones = inventory.Inventory.Paths;
        var normalizedBareRepo = GitWizardApi.ResolveOnDiskPath(bareRepoPath);

        Assert.That(allClones, Does.Not.Contain(normalizedBareRepo));
    }

    [Test]
    public void ExpandAllClonePaths_WorktreeWhoseDirectoryWasDeleted_IsSkippedAndTheRepositoryKept()
    {
        GitWizardLog.SilentMode = true;
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        var worktreeDir = repo.AddWorktree("feature-worktree");
        Directory.Delete(worktreeDir, recursive: true);

        var expanded = GitWizardApi.ExpandAllClonePaths([repo.Path]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expanded, Does.Contain(GitWizardApi.ResolveOnDiskPath(repo.Path)));
            Assert.That(expanded, Does.Not.Contain(GitWizardApi.ResolveOnDiskPath(worktreeDir)));
        }
    }

    [Test]
    public void ExpandAllClonePaths_UninitializedSubmodule_IsNotACloneYet()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        repo.AddUninitializedSubmodule("external/pending");

        var expanded = GitWizardApi.ExpandAllClonePaths([repo.Path]);

        Assert.That(expanded, Is.EqualTo([GitWizardApi.ResolveOnDiskPath(repo.Path)]));
    }
}
