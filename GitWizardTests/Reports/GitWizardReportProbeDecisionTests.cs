using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

[NonParallelizable]
public class GitWizardReportProbeDecisionTests
{
    static readonly DateTimeOffset OldWriteTimeUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset NewWriteTimeUtc = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset ClockTimeUtc = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void Refresh_InvalidatedEntryThenProgrammingError_KeepsRepositoryVisibleAndForgetsStaleEntry()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var report = new GitWizardReport();
        var entries = new SortedDictionary<string, NonRepositoryProbeEntry>(GitWizardApi.PathComparer)
        {
            [GitWizardApi.NormalizePath(fixture.Path)] = new() { LastWriteTimeUtc = OldWriteTimeUtc, LastUsedUtc = ClockTimeUtc },
        };
        var cache = CreateCache(entries, NonRepositoryProbeCache.DefaultCapacity);
        var handler = new RecordingUpdateHandler
        {
            CreatingRepository = repository => repository.PhaseObserverForTesting =
                _ => throw new InvalidOperationException("programming error"),
        };

        report.Refresh([fixture.Path], CreateContext(cache, handler));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
            Assert.That(report.NonRepositoryPaths, Is.Empty);
            Assert.That(entries, Is.Empty);
        }
    }

    [Test]
    public void Refresh_InvalidatedEntryThenCancellation_KeepsRepositoryVisibleAndForgetsStaleEntry()
    {
        GitWizardLog.SilentMode = true;
        using var candidate = new TempDirectory("gw-probe-cancel-");
        var report = new GitWizardReport();
        var entries = new SortedDictionary<string, NonRepositoryProbeEntry>(GitWizardApi.PathComparer)
        {
            [GitWizardApi.NormalizePath(candidate.Path)] = new() { LastWriteTimeUtc = OldWriteTimeUtc, LastUsedUtc = ClockTimeUtc },
        };
        var cache = CreateCache(entries, NonRepositoryProbeCache.DefaultCapacity);
        var previousTimeout = GitWizardReport.RefreshTimeout;

        try
        {
            GitWizardReport.RefreshTimeout = TimeSpan.Zero;
            report.Refresh([candidate.Path], CreateContext(cache, null));
        }
        finally
        {
            GitWizardReport.RefreshTimeout = previousTimeout;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Repositories.ContainsKey(candidate.Path), Is.True);
            Assert.That(report.Repositories[candidate.Path].RefreshError, Is.EqualTo("Timed out"));
            Assert.That(report.NonRepositoryPaths, Is.Empty);
            Assert.That(entries, Is.Empty);
        }
    }

    [Test]
    public void Refresh_MalformedOutcomesEvictedDuringSameRefresh_AreStillPruned()
    {
        GitWizardLog.SilentMode = true;
        using var first = TempRepoFixture.CreateWithInitialCommit();
        using var second = TempRepoFixture.CreateWithInitialCommit();
        var report = new GitWizardReport();
        var entries = new SortedDictionary<string, NonRepositoryProbeEntry>(GitWizardApi.PathComparer);
        var cache = CreateCache(entries, capacity: 1);
        var handler = new RecordingUpdateHandler
        {
            CreatingRepository = repository => repository.PhaseObserverForTesting =
                _ => throw new LibGit2SharpException("failed to read index"),
        };

        report.Refresh([first.Path, second.Path], CreateContext(cache, handler));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(report.NonRepositoryPaths, Has.Count.EqualTo(2));
            Assert.That(report.Repositories, Is.Empty);
        }
    }

    static GitWizardReport.RepositoryRefreshContext CreateContext(NonRepositoryProbeCache cache, IUpdateHandler? handler) =>
        new(handler, FetchRemotes: false, DeepRefresh: false, AllBranches: false, ComputeLocalCommitCount: true, cache);

    static NonRepositoryProbeCache CreateCache(SortedDictionary<string, NonRepositoryProbeEntry> entries, int capacity) =>
        new(entries, new FixedClock(ClockTimeUtc), Directory.Exists, _ => NewWriteTimeUtc, capacity);

    sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
