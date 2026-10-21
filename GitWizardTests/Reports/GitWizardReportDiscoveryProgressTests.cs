using System.Collections.Concurrent;
using GitWizard;

namespace GitWizardTests;

/// <summary>
/// Regression coverage for hazard 8 in docs/refresh-concurrency-review.md: the discovery loop in
/// <see cref="GitWizardReport.GetRepositoryPathsAsync(System.Collections.Generic.ICollection{System.String},GitWizard.IUpdateHandler,bool,bool,GitWizard.GitWizardReportOptions,System.Threading.CancellationToken)"/>
/// incremented a shared counter with a plain
/// <c>++count</c> inside <see cref="Parallel.ForEach{TSource}(System.Collections.Generic.IEnumerable{TSource},System.Action{TSource})"/>,
/// so concurrent workers could lose updates and the progress bar could undercount and never reach
/// the search-path total. <see cref="RepeatAttribute"/> rather than a timing assertion makes the
/// race observable without relying on wall-clock behavior.
/// </summary>
public class GitWizardReportDiscoveryProgressTests
{
    readonly List<string> _searchPaths = new();

    [OneTimeSetUp]
    public void CreateSearchPaths()
    {
        for (var index = 0; index < 40; index++)
        {
            var path = Path.Combine(Path.GetTempPath(), "gw-progress-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            _searchPaths.Add(path);
        }
    }

    [OneTimeTearDown]
    public void DeleteSearchPaths()
    {
        foreach (var path in _searchPaths)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }

    [Test]
    [Repeat(50)]
    public async Task GetRepositoryPaths_ParallelProgress_ReachesTotal()
    {
        GitWizardLog.SilentMode = true;
        var configuration = new GitWizardConfiguration { SearchPaths = new SortedSet<string>(_searchPaths) };
        var report = new GitWizardReport(configuration);
        var handler = new RecordingProgressHandler();
        var discoveredPaths = new SortedSet<string>();

        // noMft: true skips MFT discovery (which would pop UAC on Windows), so this always runs
        // the parallel per-search-path fallback loop that hazard 8 lives in.
        await report.GetRepositoryPathsAsync(discoveredPaths, handler, noMft: true);

        Assert.That(handler.MaxReportedCount, Is.EqualTo(_searchPaths.Count));
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
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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

    sealed class RecordingProgressHandler : IUpdateHandler
    {
        readonly ConcurrentBag<int> _reportedCounts = new();

        public int MaxReportedCount => _reportedCounts.IsEmpty ? 0 : _reportedCounts.Max();

        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) => _reportedCounts.Add(count);
        public void SendUpdateMessage(string? message) { }
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
    [Test]
    public async Task GetRepositoryPaths_EmptyOrNonExistentSearchPath_FindsNothing()
    {
        GitWizardLog.SilentMode = true;
        var report = new GitWizardReport();
        report.SearchPaths.Add("/nonexistent/search/path");
        var paths = new SortedSet<string>();
        await report.GetRepositoryPathsAsync(paths, noMft: true);
        Assert.That(paths, Is.Empty);
    }
}
