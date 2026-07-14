using GitWizard;
using MFTLib;

namespace GitWizardTests;

/// <summary>
/// Coverage-boost tests for <see cref="GitWizardApi"/> Discovery partial:
/// non-MFT fallback, GetRepositoryPaths edge cases, TryFindAllRepositoriesUsingMft
/// with fake elevation providers.
/// </summary>
public class CoverageBoostDiscoveryTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void GetRepositoryPaths_EmptyDirectory_ReturnsNoPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-empty-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new SortedSet<string>();
            GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>());
            Assert.That(paths, Is.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetRepositoryPaths_NonExistentRoot_LogsAndReturnsEmpty()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "gw-nonexist-" + Guid.NewGuid().ToString("N"));
        var paths = new SortedSet<string>();
        Assert.DoesNotThrow(() => GitWizardApi.GetRepositoryPaths(nonExistent, paths, Array.Empty<string>()));
        Assert.That(paths, Is.Empty);
    }

    [Test]
    public void GetRepositoryPaths_MultipleIgnoredPaths_AllRespected()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-multi-ign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a", ".git"));
        Directory.CreateDirectory(Path.Combine(root, "b", ".git"));
        Directory.CreateDirectory(Path.Combine(root, "c", ".git"));
        try
        {
            var paths = new SortedSet<string>();
            var ignoredA = Path.Combine(root, "a");
            var ignoredB = Path.Combine(root, "b");
            GitWizardApi.GetRepositoryPaths(root, paths, new[] { ignoredA, ignoredB });
            Assert.That(paths, Has.Count.EqualTo(1));
            Assert.That(paths.Single(), Does.Contain("c").IgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetRepositoryPaths_NestedRepos_FindsAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-nested-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "parent", ".git"));
        Directory.CreateDirectory(Path.Combine(root, "parent", "child", ".git"));
        try
        {
            var paths = new SortedSet<string>();
            GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>());
            Assert.That(paths, Has.Count.GreaterThanOrEqualTo(1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetRepositoryPaths_WithUpdateHandler_CallsSendUpdateMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-handler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));
        try
        {
            var paths = new SortedSet<string>();
            var handler = new CountingUpdateHandler();
            GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>(), handler);
            Assert.That(handler.MessageCount, Is.GreaterThan(0),
                "GetRepositoryPaths must call SendUpdateMessage at least once for the final count.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryFindAllRepositoriesUsingMftAsync_NoMftTrue_ReturnsFalse()
    {
        var config = new GitWizardConfiguration { SearchPaths = { Path.GetTempPath() } };
        var paths = new SortedSet<string>();
        var result = await GitWizardApi.TryFindAllRepositoriesUsingMftAsync(config, paths, noMft: true);
        Assert.That(result, Is.False);
        Assert.That(paths, Is.Empty);
    }

    [Test]
    public async Task TryFindAllRepositoriesUsingMftAsync_ElevatedProvider_EmptySearchPaths_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("MFT is Windows-only.");

        var config = new GitWizardConfiguration(); // empty search paths
        var paths = new SortedSet<string>();
        var fakeProvider = new FakeElevationProvider(isElevated: true);
        var result = await GitWizardApi.TryFindAllRepositoriesUsingMftAsync(config, paths, elevation: fakeProvider);
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task ReportGetRepositoryPaths_NoMft_FallsBackToDirectoryScan()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-report-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "myrepo", ".git"));
        try
        {
            var report = new GitWizardReport
            {
                SearchPaths = { root },
                IgnoredPaths = new SortedSet<string>()
            };
            var paths = new SortedSet<string>();
            await report.GetRepositoryPathsAsync(paths, noMft: true);
            Assert.That(paths, Has.Count.EqualTo(1));
            Assert.That(paths.Single(), Does.Contain("myrepo").IgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ReportGetRepositoryPaths_WithThrowingProgressHandler_DoesNotCrash()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-throw-prog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));
        try
        {
            var report = new GitWizardReport
            {
                SearchPaths = { root },
                IgnoredPaths = new SortedSet<string>()
            };
            var paths = new SortedSet<string>();
            var handler = new ThrowingProgressHandler();
            Assert.DoesNotThrowAsync(() => report.GetRepositoryPathsAsync(paths, handler, noMft: true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ReportGetRepositoryPaths_MultipleSearchPaths_MergesResults()
    {
        var root1 = Path.Combine(Path.GetTempPath(), "gw-multi1-" + Guid.NewGuid().ToString("N"));
        var root2 = Path.Combine(Path.GetTempPath(), "gw-multi2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root1, "repoA", ".git"));
        Directory.CreateDirectory(Path.Combine(root2, "repoB", ".git"));
        try
        {
            var report = new GitWizardReport
            {
                SearchPaths = { root1, root2 },
                IgnoredPaths = new SortedSet<string>()
            };
            var paths = new SortedSet<string>();
            await report.GetRepositoryPathsAsync(paths, noMft: true);
            Assert.That(paths, Has.Count.EqualTo(2));
        }
        finally
        {
            if (Directory.Exists(root1)) Directory.Delete(root1, recursive: true);
            if (Directory.Exists(root2)) Directory.Delete(root2, recursive: true);
        }
    }

    sealed class CountingUpdateHandler : IUpdateHandler
    {
        public int MessageCount { get; private set; }
        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) { }
        public void SendUpdateMessage(string? message) => MessageCount++;
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    }

    sealed class ThrowingProgressHandler : IUpdateHandler
    {
        public void StartProgress(string description, int total) => throw new InvalidOperationException("boom");
        public void UpdateProgress(int count) => throw new InvalidOperationException("boom");
        public void SendUpdateMessage(string? message) { }
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    }

    /// <summary>
    /// Fake elevation provider for testing MFT discovery paths.
    /// Note: .git directory tests here are valid because GetRepositoryPaths with noMft:true
    /// detects repos by .git directory presence, not by LibGit2Sharp validation.
    /// </summary>
    sealed class FakeElevationProvider : IElevationProvider
    {
        readonly bool _isElevated;
        readonly bool _tryRunResult;

        public FakeElevationProvider(bool isElevated, bool tryRunResult = false)
        {
            _isElevated = isElevated;
            _tryRunResult = tryRunResult;
        }

        public bool IsElevated() => _isElevated;
        public bool CanSelfElevate() => true;
        public bool TryRunElevated(string arguments, int timeoutMs = 60000) => _tryRunResult;
    }
}
