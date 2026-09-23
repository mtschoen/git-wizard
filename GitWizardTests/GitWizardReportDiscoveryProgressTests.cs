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
}
