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
            [GitWizardApi.NormalizePath(fixture.Path)] = new()
            {
                LastWriteTimeUtc = OldWriteTimeUtc,
                LastUsedUtc = ClockTimeUtc,
                PolicyVersion = NonRepositoryProbeEntry.CurrentPolicyVersion,
            },
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
            [GitWizardApi.NormalizePath(candidate.Path)] = new()
            {
                LastWriteTimeUtc = OldWriteTimeUtc,
                LastUsedUtc = ClockTimeUtc,
                PolicyVersion = NonRepositoryProbeEntry.CurrentPolicyVersion,
            },
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
    public void Refresh_NonRepositoryOutcomesEvictedDuringSameRefresh_AreStillPruned()
    {
        GitWizardLog.SilentMode = true;
        using var first = new TempDirectory("gw-nonrepo-first-");
        using var second = new TempDirectory("gw-nonrepo-second-");
        var report = new GitWizardReport();
        var entries = new SortedDictionary<string, NonRepositoryProbeEntry>(GitWizardApi.PathComparer);
        var cache = CreateCache(entries, capacity: 1);
        var handler = new RecordingUpdateHandler();

        report.Refresh([first.Path, second.Path], CreateContext(cache, handler));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(report.NonRepositoryPaths, Has.Count.EqualTo(2));
            Assert.That(report.Repositories, Is.Empty);
        }
    }

    [Test]
    public void Refresh_UnreadableExistingIndex_RetriesNextRefreshAndRecovers()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var indexPath = Path.Combine(fixture.Path, ".git", "index");
        Assert.That(File.Exists(indexPath), Is.True, "Precondition: repository index file must exist.");
        var validIndexBytes = File.ReadAllBytes(indexPath);

        var report = new GitWizardReport();
        var entries = report.NonRepositoryProbes;
        var handler = new RecordingUpdateHandler();
        try
        {
            using var exclusiveLockStream = LockOrBreakIndex(indexPath);

            RefreshWithDefaultCache(report, fixture.Path, handler);
            RefreshWithDefaultCache(report, fixture.Path, handler);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(handler.RepositoriesCreated, Has.Length.EqualTo(2));
                Assert.That(entries, Is.Empty);
                Assert.That(report.NonRepositoryPaths, Is.Empty);
                Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
                if (report.Repositories.TryGetValue(fixture.Path, out var failedRepository))
                {
                    Assert.That(failedRepository.RefreshError,
                        Does.Contain("failed to read index").IgnoreCase);
                }
            }
        }
        finally
        {
            RestoreBrokenIndex(indexPath, validIndexBytes);
        }

        RefreshWithDefaultCache(report, fixture.Path, handler);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.RepositoriesCreated, Has.Length.EqualTo(3));
            Assert.That(entries, Is.Empty);
            Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
            if (report.Repositories.TryGetValue(fixture.Path, out var recoveredRepository))
            {
                Assert.That(recoveredRepository.RefreshError, Is.Null);
                Assert.That(recoveredRepository.CurrentBranch, Is.Not.Null.And.Not.Empty);
            }
        }
    }

    [Test]
    public async Task GenerateReportAsync_PersistedLegacyProbesWithMatchingWriteTime_RevalidatesAndRetainsSupportedPolicy()
    {
        GitWizardLog.SilentMode = true;
        using var sandbox = new LocalFilesSandbox();
        using var unsupportedDirectory = new TempDirectory("gw-legacy-unsupported-");
        var initializedRealUnsupported = GitWizardReportOwnershipTests.TryInitializeGitRepository(
            unsupportedDirectory.Path, "--ref-format=reftable");
        using var indexFailureFixture = TempRepoFixture.CreateWithInitialCommit();
        using var plainDirectory = new TempDirectory("gw-legacy-plain-");
        var indexPath = Path.Combine(indexFailureFixture.Path, ".git", "index");
        var validIndexBytes = await File.ReadAllBytesAsync(indexPath);
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        string[] candidatePaths = [unsupportedDirectory.Path, indexFailureFixture.Path, plainDirectory.Path];
        var plainDirectoryKey = GitWizardApi.NormalizePath(plainDirectory.Path);

        try
        {
            await using (LockOrBreakIndex(indexPath))
            {
                await SaveLegacyProbesWithCurrentWriteTimesAsync(candidatePaths);
                var firstHandler = CreateUnsupportedFallbackHandler(
                    unsupportedDirectory.Path, initializedRealUnsupported);

                await using var firstReport = await GitWizardReport.GenerateReportAsync(
                    configuration, candidatePaths, firstHandler);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(firstHandler.RepositoriesCreated, Has.Length.EqualTo(3));
                    Assert.That(firstReport.Repositories.ContainsKey(unsupportedDirectory.Path), Is.True);
                    Assert.That(firstReport.Repositories[unsupportedDirectory.Path].RefreshError,
                        Does.Contain("Unsupported repository format").IgnoreCase
                            .And.Contain("reftable").IgnoreCase);
                    Assert.That(firstReport.Repositories.ContainsKey(indexFailureFixture.Path), Is.True);
                    Assert.That(firstReport.Repositories[indexFailureFixture.Path].RefreshError,
                        Does.Contain("failed to read index").IgnoreCase);
                    Assert.That(firstReport.Repositories.ContainsKey(plainDirectory.Path), Is.False);
                    Assert.That(firstReport.NonRepositoryPaths, Is.EquivalentTo([plainDirectory.Path]));
                    Assert.That(firstReport.NonRepositoryProbes.Keys, Is.EqualTo([plainDirectoryKey]));
                    Assert.That(firstReport.NonRepositoryProbes[plainDirectoryKey].PolicyVersion,
                        Is.EqualTo(NonRepositoryProbeEntry.CurrentPolicyVersion));
                }

                await firstReport.SaveAsync(GitWizardReport.GetCachedReportPath());
            }

            RestoreBrokenIndex(indexPath, validIndexBytes);
            TestUtilities.ResetStaticCaches();
            var secondHandler = CreateUnsupportedFallbackHandler(
                unsupportedDirectory.Path, initializedRealUnsupported);

            await using var secondReport = await GitWizardReport.GenerateReportAsync(
                configuration, candidatePaths, secondHandler);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(secondHandler.RepositoriesCreated, Has.Length.EqualTo(2));
                Assert.That(secondReport.Repositories[unsupportedDirectory.Path].RefreshError,
                    Does.Contain("Unsupported repository format").IgnoreCase);
                Assert.That(secondReport.Repositories[indexFailureFixture.Path].RefreshError, Is.Null);
                Assert.That(secondReport.Repositories[indexFailureFixture.Path].CurrentBranch,
                    Is.Not.Null.And.Not.Empty);
                Assert.That(secondReport.Repositories.ContainsKey(plainDirectory.Path), Is.False);
                Assert.That(secondReport.NonRepositoryPaths, Is.EquivalentTo([plainDirectory.Path]));
                Assert.That(secondReport.NonRepositoryProbes.Keys, Is.EqualTo([plainDirectoryKey]));
            }
        }
        finally
        {
            RestoreBrokenIndex(indexPath, validIndexBytes);
        }
    }

    [Test]
    public void Refresh_MissingCommitObject_StaysVisibleWithRefreshErrorAndRetries()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        string initialCommitHash;
        using (var repository = new Repository(fixture.Path))
        {
            initialCommitHash = repository.Head.Tip!.Sha;
        }

        fixture.AppendCommit("second.txt");
        var objectPath = Path.Combine(fixture.Path, ".git", "objects", initialCommitHash[..2], initialCommitHash[2..]);
        File.SetAttributes(objectPath, File.GetAttributes(objectPath) & ~FileAttributes.ReadOnly);
        File.Delete(objectPath);

        var report = new GitWizardReport();
        var entries = report.NonRepositoryProbes;
        var handler = new RecordingUpdateHandler();

        RefreshWithDefaultCache(report, fixture.Path, handler);
        RefreshWithDefaultCache(report, fixture.Path, handler);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.RepositoriesCreated, Has.Length.EqualTo(2));
            Assert.That(entries, Is.Empty);
            Assert.That(report.NonRepositoryPaths, Is.Empty);
            Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
            if (report.Repositories.TryGetValue(fixture.Path, out var refreshedRepository))
            {
                Assert.That(refreshedRepository.RefreshError, Does.Contain(initialCommitHash));
            }
        }
    }

    [Test]
    public void Refresh_PlainDirectory_PrunesAndCachesProbeOutcome()
    {
        GitWizardLog.SilentMode = true;
        using var plainDirectory = new TempDirectory("gw-plain-directory-");
        var report = new GitWizardReport();
        var entries = report.NonRepositoryProbes;
        var handler = new RecordingUpdateHandler();

        RefreshWithDefaultCache(report, plainDirectory.Path, handler);
        RefreshWithDefaultCache(report, plainDirectory.Path, handler);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.RepositoriesCreated, Has.Length.EqualTo(1));
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(report.NonRepositoryPaths, Is.EquivalentTo([plainDirectory.Path]));
            Assert.That(report.Repositories, Is.Empty);
        }
    }

    static FileStream? LockOrBreakIndex(string indexPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return File.Open(indexPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        File.Delete(indexPath);
        Directory.CreateDirectory(indexPath);
        return null;
    }

    static void RestoreBrokenIndex(string indexPath, byte[] validIndexBytes)
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(indexPath))
        {
            Directory.Delete(indexPath);
            File.WriteAllBytes(indexPath, validIndexBytes);
        }
    }

    static async Task SaveLegacyProbesWithCurrentWriteTimesAsync(IEnumerable<string> candidatePaths)
    {
        var legacyReport = new GitWizardReport();
        foreach (var path in candidatePaths)
        {
            legacyReport.NonRepositoryProbes[GitWizardApi.NormalizePath(path)] = new NonRepositoryProbeEntry
            {
                LastWriteTimeUtc = new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                LastUsedUtc = ClockTimeUtc,
            };
        }

        await legacyReport.SaveAsync(GitWizardReport.GetCachedReportPath());
        TestUtilities.ResetStaticCaches();
    }

    static RecordingUpdateHandler CreateUnsupportedFallbackHandler(string unsupportedPath, bool initializedRealUnsupported) =>
        new()
        {
            CreatingRepository = initializedRealUnsupported
                ? null
                : repository =>
                {
                    if (GitWizardApi.PathComparer.Equals(repository.WorkingDirectory, unsupportedPath))
                    {
                        repository.PhaseObserverForTesting =
                            _ => throw new LibGit2SharpException("unsupported extension name extensions.refstorage");
                    }
                },
        };

    static void RefreshWithDefaultCache(GitWizardReport report, string path, IUpdateHandler handler) =>
        report.Refresh([path], CreateContext(CreateCache(report.NonRepositoryProbes, NonRepositoryProbeCache.DefaultCapacity), handler));

    static GitWizardReport.RepositoryRefreshContext CreateContext(NonRepositoryProbeCache cache, IUpdateHandler? handler) =>
        new(handler, FetchRemotes: false, DeepRefresh: false, AllBranches: false, ComputeLocalCommitCount: true, cache);

    static NonRepositoryProbeCache CreateCache(SortedDictionary<string, NonRepositoryProbeEntry> entries, int capacity) =>
        new(entries, new FixedClock(ClockTimeUtc), Directory.Exists, _ => NewWriteTimeUtc, capacity);

    sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
