using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Discovery;

[TestFixture]
public class RecursiveDiscoveryTests
{
    TempDirectory? _tempDirectory;
    string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = new TempDirectory("GitWizardTests_Recurse_");
        _tempDir = _tempDirectory.Path;
    }

    [TearDown]
    public void TearDown()
    {
        _tempDirectory?.Dispose();
    }

    [Test]
    public void DiscoverRecursively_FindsOrdinaryBareAndGitFile()
    {
        var ordinaryRepo = Path.Combine(_tempDir, "ordinary");
        Directory.CreateDirectory(Path.Combine(ordinaryRepo, ".git"));

        var bareRepo = Path.Combine(_tempDir, "bare.git");
        Directory.CreateDirectory(bareRepo);
        File.WriteAllText(Path.Combine(bareRepo, "HEAD"), "ref: refs/heads/main");
        Directory.CreateDirectory(Path.Combine(bareRepo, "objects"));
        Directory.CreateDirectory(Path.Combine(bareRepo, "refs"));

        var gitFileRepo = Path.Combine(_tempDir, "worktree");
        Directory.CreateDirectory(gitFileRepo);
        File.WriteAllText(Path.Combine(gitFileRepo, ".git"), "gitdir: /somewhere/else");

        var nonRepo = Path.Combine(_tempDir, "regular_folder");
        Directory.CreateDirectory(nonRepo);
        File.WriteAllText(Path.Combine(nonRepo, "HEAD"), "just a file");

        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { _tempDir },
            IncludeTempDirectories = true
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, true, []);

        var result = GitWizardApi.DiscoverRecursively(request, _tempDir, CancellationToken.None);

        Assert.That(result.Paths, Does.Contain(GitWizardApi.NormalizePath(ordinaryRepo)));
        Assert.That(result.Paths, Does.Contain(GitWizardApi.NormalizePath(bareRepo)));
        Assert.That(result.Paths, Does.Contain(GitWizardApi.NormalizePath(gitFileRepo)));
        Assert.That(result.Paths, Does.Not.Contain(GitWizardApi.NormalizePath(nonRepo)));
    }

    [Test]
    public void DiscoverRecursively_ExcludesInternalGitDirectory_WhenHiddenEnabled()
    {
        var ordinaryRepo = Path.Combine(_tempDir, "ordinary");
        var internalGit = Path.Combine(ordinaryRepo, ".git");
        Directory.CreateDirectory(internalGit);
        // Put HEAD, objects, refs inside .git (standard structure)
        File.WriteAllText(Path.Combine(internalGit, "HEAD"), "ref: refs/heads/main");
        Directory.CreateDirectory(Path.Combine(internalGit, "objects"));
        Directory.CreateDirectory(Path.Combine(internalGit, "refs"));

        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { _tempDir },
            SkipHiddenDirectories = false, // Even if hidden directories are examined
            IncludeTempDirectories = true
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, true, []);

        var result = GitWizardApi.DiscoverRecursively(request, _tempDir, CancellationToken.None);

        Assert.That(result.Paths, Does.Contain(GitWizardApi.NormalizePath(ordinaryRepo)));
        Assert.That(result.Paths, Does.Not.Contain(GitWizardApi.NormalizePath(internalGit)));
    }

    [Test]
    public void DiscoverRecursively_HonorsIgnoredPaths()
    {
        var repo1 = Path.Combine(_tempDir, "repo1");
        Directory.CreateDirectory(Path.Combine(repo1, ".git"));

        var ignored = Path.Combine(_tempDir, "ignored");
        var ignoredRepo = Path.Combine(ignored, "repo2");
        Directory.CreateDirectory(Path.Combine(ignoredRepo, ".git"));

        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { _tempDir },
            IgnoredPaths = { ignored },
            IncludeTempDirectories = true
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, true, []);

        var result = GitWizardApi.DiscoverRecursively(request, _tempDir, CancellationToken.None);

        Assert.That(result.Paths, Does.Contain(GitWizardApi.NormalizePath(repo1)));
        Assert.That(result.Paths, Does.Not.Contain(GitWizardApi.NormalizePath(ignoredRepo)));
    }

    [Test]
    public void DiscoverRecursively_ThrowsOnCancellation()
    {
        var repo = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { _tempDir },
            IncludeTempDirectories = true
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, true, []);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            GitWizardApi.DiscoverRecursively(request, _tempDir, cts.Token));
    }

    [Test]
    public void DiscoverRecursively_RecordsDeniedSubtreeInFailedSubtrees()
    {
        var accessible = Path.Combine(_tempDir, "accessible");
        Directory.CreateDirectory(accessible);
        var accessibleRepo = Path.Combine(accessible, "repo");
        Directory.CreateDirectory(Path.Combine(accessibleRepo, ".git"));

        var denied = Path.Combine(_tempDir, "denied");
        Directory.CreateDirectory(denied);

        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { _tempDir },
            IncludeTempDirectories = true
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, true, []);

        try
        {
            GitWizardApi.DirectoryEnumeratorOverride = path =>
            {
                if (path.Equals(denied, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("Simulated access denied");
                return Directory.GetDirectories(path);
            };

            var result = GitWizardApi.DiscoverRecursively(request, _tempDir, CancellationToken.None);

            var normalizedDenied = GitWizardApi.NormalizePath(denied);
            Assert.That(result.Paths, Does.Contain(GitWizardApi.NormalizePath(accessibleRepo)));
            Assert.That(result.FailedSubtrees, Does.Contain(normalizedDenied));
            Assert.That(result.Errors, Does.ContainKey(normalizedDenied));
        }
        finally
        {
            GitWizardApi.DirectoryEnumeratorOverride = null;
        }
    }
}
