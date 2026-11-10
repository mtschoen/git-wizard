using GitWizard.Watch;
using GitWizardTests.TestSupport;
using static GitWizardTests.TestSupport.WatchEventDrain;

namespace GitWizardTests.Watch;

// Watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryWatchService (membership admission, creation, and deletion tracking).
[TestFixture]
public class RepositoryWatchServiceMembershipTests
{
    [Test]
    public async Task DotGitCreatedUnderTempDirectory_ExcludedByDefault()
    {
        var repositoryPath = Path.Combine(TempDirectory.SystemTempPath, "gw-membership", "repo");
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(
                [ new VolumeChangeBatch(WatchPath.Parse(repositoryPath).Volume!,
                [
                    new VolumeChangeEntry(Path.Combine(repositoryPath, ".git"), VolumeEntryKind.Created)
                    ]) ]),
            trackedRoots: [],
            searchRoots: [Path.GetPathRoot(repositoryPath)!],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DotGitCreatedUnderTempDirectory_EmittedWhenIncludeTempDirectories()
    {
        var repositoryPath = Path.Combine(TempDirectory.SystemTempPath, "gw-membership", "repo");
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(
                [ new VolumeChangeBatch(WatchPath.Parse(repositoryPath).Volume!,
                [
                    new VolumeChangeEntry(Path.Combine(repositoryPath, ".git"), VolumeEntryKind.Created)
                    ]) ]),
            trackedRoots: [],
            searchRoots: [Path.GetPathRoot(repositoryPath)!],
            debounce: TimeSpan.FromMilliseconds(10),
            includeTempDirectories: true);

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(repositoryPath, RepositoryChangeKind.Created),
            ]));
    }

    [Test]
    public async Task TrackedTempDirectory_ExcludedByDefault_OnModificationAndGitRecreation()
    {
        var trackedTempRepo = Path.Combine(TempDirectory.SystemTempPath, "gw-membership", "repo");
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(
                [
                    new VolumeChangeBatch(WatchPath.Parse(trackedTempRepo).Volume!,
                        [
                            new VolumeChangeEntry(Path.Combine(trackedTempRepo, "file.txt"), VolumeEntryKind.Modified),
                            new VolumeChangeEntry(Path.Combine(trackedTempRepo, ".git"), VolumeEntryKind.Created),
                        ])
                ]),
            trackedRoots: [trackedTempRepo],
            searchRoots: [Path.GetPathRoot(trackedTempRepo)!],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task IgnoredPath_ExcludedEvenWhenIncludeTempDirectoriesTrue()
    {
        var ignoredTempSubtree = Path.Combine(TempDirectory.SystemTempPath, "gw-membership", "ignored");
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(
                [
                    new VolumeChangeBatch(WatchPath.Parse(ignoredTempSubtree).Volume!,
                        [
                            new VolumeChangeEntry(Path.Combine(ignoredTempSubtree, "repo", ".git"), VolumeEntryKind.Created),
                        ])
                ]),
            trackedRoots: [],
            searchRoots: [Path.GetPathRoot(ignoredTempSubtree)!],
            debounce: TimeSpan.FromMilliseconds(10),
            includeTempDirectories: true,
            ignoredPaths: [ignoredTempSubtree]);

        var events = await DrainAsync(svc);

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task LiveDiscoveredRepository_SubsequentEdit_EmitsChanged()
    {
        var batches = new[]
        {
                new VolumeChangeBatch("C", [
                    new VolumeChangeEntry(@"C:\repo\newrepo\.git", VolumeEntryKind.Created)
                ]),
                new VolumeChangeBatch("C", [
                    new VolumeChangeEntry(@"C:\repo\newrepo\a.txt", VolumeEntryKind.Modified)
                ]),
            };
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(batches),
            trackedRoots: [],
            searchRoots: [@"C:\repo"],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\newrepo", RepositoryChangeKind.Created)));
        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\newrepo", RepositoryChangeKind.Changed)));
    }

    [Test]
    public async Task TrackedRootDeletedThenRecreated_EmitsDeletedThenCreatedAgain()
    {
        var batches = new[]
        {
                new VolumeChangeBatch("C", [
                    new VolumeChangeEntry(@"C:\repo\gone\.git", VolumeEntryKind.Deleted)
                ]),
                new VolumeChangeBatch("C", [
                    new VolumeChangeEntry(@"C:\repo\gone\.git", VolumeEntryKind.Created)
                ]),
                new VolumeChangeBatch("C", [
                    new VolumeChangeEntry(@"C:\repo\gone\a.txt", VolumeEntryKind.Modified)
                ]),
            };
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(batches),
            trackedRoots: [@"C:\repo\gone"],
            searchRoots: [@"C:\repo"],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(@"C:\repo\gone", RepositoryChangeKind.Deleted),
                new RepositoryChangeEvent(@"C:\repo\gone", RepositoryChangeKind.Created),
                new RepositoryChangeEvent(@"C:\repo\gone", RepositoryChangeKind.Changed),
            ]));
    }

    [Test]
    public async Task TrackedRootDeletedThenRecreatedInSameBatch_EmitsDeletedThenCreatedAndRemainsTracked()
    {
        var batches = new[]
        {
                new VolumeChangeBatch("C", [
                    new VolumeChangeEntry(@"C:\repo\lifecycle\.git", VolumeEntryKind.Deleted),
                    new VolumeChangeEntry(@"C:\repo\lifecycle\.git", VolumeEntryKind.Created),
                ]),
                new VolumeChangeBatch("C", [
                    new VolumeChangeEntry(@"C:\repo\lifecycle\file.txt", VolumeEntryKind.Modified)
                ]),
            };
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(batches),
            trackedRoots: [@"C:\repo\lifecycle"],
            searchRoots: [@"C:\repo"],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Deleted),
                new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Created),
                new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Changed),
            ]));
    }

    [Test]
    public async Task TrackedRootDeletedThenRecreatedAndModifiedInSameBatch_EmitsInOrder()
    {
        var batch = new VolumeChangeBatch("C", [
                new VolumeChangeEntry(@"C:\repo\lifecycle\.git", VolumeEntryKind.Deleted),
                new VolumeChangeEntry(@"C:\repo\lifecycle\.git", VolumeEntryKind.Created),
                new VolumeChangeEntry(@"C:\repo\lifecycle\file.txt", VolumeEntryKind.Modified),
            ]);
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource([batch]),
            trackedRoots: [@"C:\repo\lifecycle"],
            searchRoots: [@"C:\repo"],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Deleted),
                new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Created),
                new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Changed),
            ]));
    }

    [Test]
    public async Task MovedInAfterCreatedAndDeletedInSameBatch_EmitsCreatedDeletedCreated()
    {
        using var temporaryRoot = new TempDirectory("watch-recreate-");
        var searchRoot = temporaryRoot.Path;
        var repoPath = Path.Combine(searchRoot, "repo");
        var gitPath = Path.Combine(repoPath, ".git");
        Directory.CreateDirectory(gitPath);
        var volume = WatchPath.Parse(repoPath).Volume!;
        var batch = new VolumeChangeBatch(volume, [
                new VolumeChangeEntry(gitPath, VolumeEntryKind.Created),
                new VolumeChangeEntry(repoPath, VolumeEntryKind.Deleted),
                new VolumeChangeEntry(repoPath, VolumeEntryKind.MovedIn),
            ]);
        var service = new RepositoryWatchService(
            new ScriptedVolumeChangeSource([batch]),
            trackedRoots: [],
            searchRoots: [searchRoot],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(service);

        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Created),
                new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Deleted),
                new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Created),
            ]));
    }

    [Test]
    public async Task MovedInAfterCreatedAndDeleted_EmitsChangedOnSubsequentBatch()
    {
        using var temporaryRoot = new TempDirectory("watch-recreate-changed-");
        var searchRoot = temporaryRoot.Path;
        var repoPath = Path.Combine(searchRoot, "repo");
        var gitPath = Path.Combine(repoPath, ".git");
        Directory.CreateDirectory(gitPath);
        var volume = WatchPath.Parse(repoPath).Volume!;
        var batches = new[]
        {
                new VolumeChangeBatch(volume, [
                    new VolumeChangeEntry(gitPath, VolumeEntryKind.Created),
                    new VolumeChangeEntry(repoPath, VolumeEntryKind.Deleted),
                    new VolumeChangeEntry(repoPath, VolumeEntryKind.MovedIn),
                ]),
                new VolumeChangeBatch(volume, [
                    new VolumeChangeEntry(Path.Combine(repoPath, "file.txt"), VolumeEntryKind.Modified),
                ]),
            };
        var service = new RepositoryWatchService(
            new DelayedVolumeChangeSource(batches, TimeSpan.FromMilliseconds(60)),
            trackedRoots: [],
            searchRoots: [searchRoot],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(service);

        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Created),
                new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Deleted),
                new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Created),
                new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Changed),
            ]));
    }

    [Test]
    public async Task WindowsCaseRenameAfterCreatedInSameBatch_EmitsCreatedDeletedCreated()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Case-only Windows rename test is Windows-only.");
        }

        using var temporaryRoot = new TempDirectory("watch-case-rename-create-");
        var searchRoot = temporaryRoot.Path;
        var lowerPath = Path.Combine(searchRoot, "repo");
        var upperPath = Path.Combine(searchRoot, "Repo");
        var gitPath = Path.Combine(upperPath, ".git");
        Directory.CreateDirectory(gitPath);
        var volume = WatchPath.Parse(upperPath).Volume!;
        var batch = new VolumeChangeBatch(volume, [
                new VolumeChangeEntry(Path.Combine(lowerPath, ".git"), VolumeEntryKind.Created),
                new VolumeChangeEntry(lowerPath, VolumeEntryKind.Deleted),
                new VolumeChangeEntry(upperPath, VolumeEntryKind.MovedIn),
            ]);
        var service = new RepositoryWatchService(
            new ScriptedVolumeChangeSource([batch]),
            trackedRoots: [],
            searchRoots: [searchRoot],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(service);

        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(lowerPath, RepositoryChangeKind.Created),
                new RepositoryChangeEvent(lowerPath, RepositoryChangeKind.Deleted),
                new RepositoryChangeEvent(upperPath, RepositoryChangeKind.Created),
            ]));
    }
}
