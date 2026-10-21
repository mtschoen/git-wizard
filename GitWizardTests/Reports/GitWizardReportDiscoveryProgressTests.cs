using GitWizard;
using GitWizardTests.TestSupport;

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
    readonly List<TempDirectory> _searchDirectories = new();

    [OneTimeSetUp]
    public void CreateSearchPaths()
    {
        for (var index = 0; index < 40; index++)
        {
            var directory = new TempDirectory("gw-progress-");
            _searchDirectories.Add(directory);
            _searchPaths.Add(directory.Path);
        }
    }

    [OneTimeTearDown]
    public void DeleteSearchPaths()
    {
        foreach (var directory in _searchDirectories)
            directory.Dispose();
    }

    [Test]
    [Repeat(50)]
    public async Task GetRepositoryPaths_ParallelProgress_ReachesTotal()
    {
        GitWizardLog.SilentMode = true;
        var configuration = new GitWizardConfiguration { SearchPaths = new SortedSet<string>(_searchPaths) };
        var report = new GitWizardReport(configuration);
        var handler = new RecordingUpdateHandler();
        var discoveredPaths = new SortedSet<string>();

        // noMft: true skips MFT discovery (which would pop UAC on Windows), so this always runs
        // the parallel per-search-path fallback loop that hazard 8 lives in.
        await report.GetRepositoryPathsAsync(discoveredPaths, handler, noMft: true);

        Assert.That(handler.ProgressCounts.Max(), Is.EqualTo(_searchPaths.Count));
    }

    [Test]
    public async Task ReportGetRepositoryPaths_NoMft_FallsBackToDirectoryScan()
    {
        using var rootDir = new TempDirectory("gw-report-scan-");
        var root = rootDir.Path;
        Directory.CreateDirectory(Path.Combine(root, "myrepo", ".git"));

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

    [Test]
    public void ReportGetRepositoryPaths_WithThrowingProgressHandler_DoesNotCrash()
    {
        using var rootDir = new TempDirectory("gw-throw-prog-");
        var root = rootDir.Path;
        Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));

        var report = new GitWizardReport
        {
            SearchPaths = { root },
            IgnoredPaths = new SortedSet<string>()
        };
        var paths = new SortedSet<string>();
        var handler = new RecordingUpdateHandler
        {
            StartingProgress = (_, _) => throw new InvalidOperationException("boom"),
            UpdatingProgress = _ => throw new InvalidOperationException("boom")
        };
        Assert.DoesNotThrowAsync(() => report.GetRepositoryPathsAsync(paths, handler, noMft: true));
    }

    [Test]
    public async Task ReportGetRepositoryPaths_MultipleSearchPaths_MergesResults()
    {
        using var dir1 = new TempDirectory("gw-multi1-");
        using var dir2 = new TempDirectory("gw-multi2-");
        var root1 = dir1.Path;
        var root2 = dir2.Path;
        Directory.CreateDirectory(Path.Combine(root1, "repoA", ".git"));
        Directory.CreateDirectory(Path.Combine(root2, "repoB", ".git"));

        var report = new GitWizardReport
        {
            SearchPaths = { root1, root2 },
            IgnoredPaths = new SortedSet<string>()
        };
        var paths = new SortedSet<string>();
        await report.GetRepositoryPathsAsync(paths, noMft: true);
        Assert.That(paths, Has.Count.EqualTo(2));
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
