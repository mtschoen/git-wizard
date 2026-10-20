using System.Collections.Concurrent;
using GitWizard;

namespace GitWizardTests;

public class GitWizardApiDiscoveryTests
{
    string? _tempRoot;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _tempRoot = Path.Combine(Path.GetTempPath(), "gw-disc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (!string.IsNullOrEmpty(_tempRoot) && Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public void GetRepositoryPaths_EmptyDirectory_ReturnsNoPaths()
    {
        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, Array.Empty<string>());
        Assert.That(paths, Is.Empty);
    }

    [Test]
    public void GetRepositoryPaths_NonExistentRoot_LogsAndReturnsEmpty()
    {
        var nonExistent = Path.Combine(_tempRoot!, "non-existent-" + Guid.NewGuid().ToString("N"));
        var paths = new SortedSet<string>();
        Assert.DoesNotThrow(() => GitWizardApi.GetRepositoryPaths(nonExistent, paths, Array.Empty<string>()));
        Assert.That(paths, Is.Empty);
    }

    [Test]
    public void GetRepositoryPaths_FindsRepoInSubdirectory()
    {
        var subRepo = Path.Combine(_tempRoot!, "sub", "repo");
        Directory.CreateDirectory(Path.Combine(subRepo, ".git"));

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, Array.Empty<string>());

        Assert.That(paths, Has.Count.EqualTo(1));
        Assert.That(paths.First(), Does.Contain(Path.Combine("sub", "repo")));
    }

    [Test]
    public void GetRepositoryPaths_FindsSiblingAndArbitrarilyNestedRepos()
    {
        for (var i = 0; i < 5; i++)
            Directory.CreateDirectory(Path.Combine(_tempRoot!, $"repo{i}", ".git"));
        Directory.CreateDirectory(Path.Combine(_tempRoot!, "a", "b", "c", ".git"));
        Directory.CreateDirectory(Path.Combine(_tempRoot!, "parent", "child", ".git"));

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, Array.Empty<string>());

        Assert.That(paths, Has.Count.EqualTo(7));
    }

    [Test]
    public void GetRepositoryPaths_FindsGitFileSubmodule()
    {
        var submodulePath = Path.Combine(_tempRoot!, "submodule");
        Directory.CreateDirectory(submodulePath);
        File.WriteAllText(Path.Combine(submodulePath, ".git"), "gitdir: /some/path");

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, Array.Empty<string>());

        Assert.That(paths, Has.Count.EqualTo(1));
        Assert.That(paths.Single(), Does.Contain("submodule"));
    }

    [TestCase(false, 2)]
    [TestCase(true, 1)]
    [TestCase(null, 2)]
    public void GetRepositoryPaths_DotDirectories_RespectsSkipFlag(bool? skipHidden, int expectedCount)
    {
        var dotDir = Path.Combine(_tempRoot!, ".hidden", "repo");
        var normalDir = Path.Combine(_tempRoot!, "normal", "repo");
        Directory.CreateDirectory(Path.Combine(dotDir, ".git"));
        Directory.CreateDirectory(Path.Combine(normalDir, ".git"));

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, Array.Empty<string>(), skipHiddenDirectories: skipHidden);

        Assert.That(paths, Has.Count.EqualTo(expectedCount));
        if (expectedCount == 1)
        {
            Assert.That(paths.Single(), Does.Contain("normal").IgnoreCase);
        }
        else
        {
            Assert.That(paths, Does.Contain(dotDir).IgnoreCase);
            Assert.That(paths, Does.Contain(normalDir).IgnoreCase);
        }
    }

    [Test]
    public void GetRepositoryPaths_SkipsHiddenAttributeDirectory()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("FileAttributes.Hidden is a Windows concept; the Hidden-attribute skip only applies there.");

        var hidden = Path.Combine(_tempRoot!, "hiddendir");
        Directory.CreateDirectory(Path.Combine(hidden, "repo", ".git"));
        new DirectoryInfo(hidden).Attributes |= FileAttributes.Hidden;
        try
        {
            var paths = new List<string>();
            GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, Array.Empty<string>(), skipHiddenDirectories: true);

            Assert.That(paths.Any(p => p.Contains("repo", StringComparison.OrdinalIgnoreCase)), Is.False,
                "A repo under a hidden-attribute directory must be skipped when skipHiddenDirectories is true.");
        }
        finally
        {
            new DirectoryInfo(hidden).Attributes &= ~FileAttributes.Hidden;
        }
    }

    [Test]
    public void GetRepositoryPaths_MultipleIgnoredPaths_AllRespected()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot!, "a", ".git"));
        Directory.CreateDirectory(Path.Combine(_tempRoot!, "b", ".git"));
        Directory.CreateDirectory(Path.Combine(_tempRoot!, "c", ".git"));

        var paths = new SortedSet<string>();
        var ignoredA = Path.Combine(_tempRoot!, "a");
        var ignoredB = Path.Combine(_tempRoot!, "b");
        GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, new[] { ignoredA, ignoredB });

        Assert.That(paths, Has.Count.EqualTo(1));
        Assert.That(paths.Single(), Does.Contain("c").IgnoreCase);
    }

    [Test]
    public void GetRepositoryPaths_WithNonExistentIgnoredPath_DoesNotThrow()
    {
        var paths = new SortedSet<string>();
        Assert.DoesNotThrow(() => GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, new[] { "/nonexistent/ignored/path" }));
        Assert.That(paths, Is.Empty);
    }

    [Test]
    public void GetRepositoryPaths_IgnoresExpandedIgnoredPathDescendants()
    {
        var ignored = Path.Combine(_tempRoot!, "ignored");
        var included = Path.Combine(_tempRoot!, "included");

        Directory.CreateDirectory(Path.Combine(ignored, "repo", ".git"));
        Directory.CreateDirectory(Path.Combine(included, "repo", ".git"));

        Environment.SetEnvironmentVariable("GITWIZARD_IGNORED", ignored);
        try
        {
            var paths = new SortedSet<string>();
            GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, new[] { "%GITWIZARD_IGNORED%" });

            Assert.That(paths, Has.Count.EqualTo(1));
            Assert.That(paths.Single(), Does.EndWith(Path.Combine("included", "repo")).IgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITWIZARD_IGNORED", null);
        }
    }

    [Test]
    public void GetRepositoryPaths_DoesNotTreatSharedPrefixAsIgnoredParent()
    {
        var ignored = Path.Combine(_tempRoot!, "foo");
        var sibling = Path.Combine(_tempRoot!, "foobar");

        Directory.CreateDirectory(Path.Combine(ignored, "repo", ".git"));
        Directory.CreateDirectory(Path.Combine(sibling, "repo", ".git"));

        var paths = new SortedSet<string>();
        GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, new[] { ignored });

        Assert.That(paths, Has.Count.EqualTo(1));
        Assert.That(paths.Single(), Does.EndWith(Path.Combine("foobar", "repo")).IgnoreCase);
    }

    [TestCase(null)]
    [TestCase(false)]
    public void GetRepositoryPaths_SearchRootInsideTemp_StillFindsRepositories(bool? includeTemp)
    {
        var tempRepo = Path.Combine(_tempRoot!, "scratch", "repo");
        Directory.CreateDirectory(Path.Combine(tempRepo, ".git"));

        var paths = new SortedSet<string>();
        if (includeTemp.HasValue)
            GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, Array.Empty<string>(), updateHandler: null,
                skipHiddenDirectories: null, includeTempDirectories: includeTemp.Value);
        else
            GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, Array.Empty<string>());

        Assert.That(paths, Does.Contain(tempRepo).IgnoreCase);
    }

    [Test]
    public void GetRepositoryPaths_WithUpdateHandler_CallsSendUpdateMessage()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot!, "repo", ".git"));
        var paths = new SortedSet<string>();
        var handler = new RecordingUpdateHandler();
        GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, Array.Empty<string>(), handler);

        Assert.Multiple(() =>
        {
            Assert.That(paths, Has.Count.EqualTo(1));
            Assert.That(handler.Messages, Does.Contain("Found 1 repositories"));
        });
    }

    [Test]
    public void GetRepositoryPaths_SwallowsThrowingUpdateHandler()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot!, "repo", ".git"));
        var paths = new List<string>();
        var handler = new ThrowingMessageHandler();

        Assert.DoesNotThrow(() => GitWizardApi.GetRepositoryPaths(_tempRoot!, paths, Array.Empty<string>(), handler));

        Assert.Multiple(() =>
        {
            Assert.That(handler.MessageCalls, Is.GreaterThan(0),
                "The scan must call SendUpdateMessage (which throws and must be caught).");
            Assert.That(paths.Any(p => p.Contains("repo", StringComparison.OrdinalIgnoreCase)), Is.True,
                "The repo must still be discovered despite the throwing handler.");
        });
    }

    [Test]
    public async Task TryDiscoverRepositoriesUsingMftAsync_EmptySearchPaths_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("MFT is Windows-only.");

        var config = new GitWizardConfiguration();
        var paths = new SortedSet<string>();
        await using var result = await GitWizardApi.TryDiscoverRepositoriesUsingMftAsync(config, paths);
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public async Task TryDiscoverRepositoriesUsingMftAsync_ReturnsFalse_OnNonWindows()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("MFT scanning is Windows-only; the non-Windows early-return is only reachable off Windows.");

        await using var result = await GitWizardApi.TryDiscoverRepositoriesUsingMftAsync(
            new GitWizardConfiguration(), new SortedSet<string>());

        Assert.That(result.Success, Is.False);
    }

    sealed class RecordingUpdateHandler : IUpdateHandler
    {
        readonly ConcurrentQueue<string> _messages = new();
        public IReadOnlyList<string> Messages => _messages.ToArray();

        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) { }
        public void SendUpdateMessage(string? message)
        {
            if (message != null)
                _messages.Enqueue(message);
        }
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    }

    sealed class ThrowingMessageHandler : IUpdateHandler
    {
        public int MessageCalls { get; private set; }

        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) { }

        public void SendUpdateMessage(string? message)
        {
            MessageCalls++;
            throw new InvalidOperationException("message failure");
        }

        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    }
}
