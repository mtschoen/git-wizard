using GitWizard.Watch;

namespace GitWizardTests.Watch;

// Watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryWatchService (moved-in directory candidate detection and adoption).
[TestFixture]
public class RepositoryWatchServiceMovedInTests
{
    static async Task<List<RepositoryChangeEvent>> DrainAsync(RepositoryWatchService svc)
    {
        var events = new List<RepositoryChangeEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var ev in svc.RunAsync(cts.Token))
            events.Add(ev);
        return events;
    }

    [Test]
    public async Task MovedInTrackedRootRenameWithinSearchRoot_EmitsDeletedThenCreated()
    {
        var searchRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"watch-rename-{Guid.NewGuid():N}");
        var oldPath = Path.Combine(searchRoot, "repo");
        var newPath = Path.Combine(searchRoot, "repo-renamed");
        Directory.CreateDirectory(Path.Combine(newPath, ".git"));
        try
        {
            var volume = WatchPath.Parse(newPath).Volume!;
            var batch = new VolumeChangeBatch(volume, new[]
            {
                    new VolumeChangeEntry(oldPath, VolumeEntryKind.Deleted),
                    new VolumeChangeEntry(newPath, VolumeEntryKind.MovedIn),
                });
            var svc = new RepositoryWatchService(
                new FakeVolumeChangeSource(new[] { batch }),
                trackedRoots: new[] { oldPath },
                searchRoots: new[] { searchRoot },
                debounce: TimeSpan.FromMilliseconds(10));

            var events = await DrainAsync(svc);

            // Order matters: the probe-emitted Created lands after the window's Deleted events so
            // the UI's pending-delete rename correlation sees the old root first.
            Assert.That(events, Is.EqualTo(new[]
            {
                    new RepositoryChangeEvent(oldPath, RepositoryChangeKind.Deleted),
                    new RepositoryChangeEvent(newPath, RepositoryChangeKind.Created),
                }));
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }

    [Test]
    public async Task MovedInRepositoryFromOutsideSearchRoots_EmitsCreated()
    {
        // The service never sees the rename's previous path; a move-in from outside every search
        // root is the same MovedIn batch with no matching Deleted.
        var searchRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"watch-move-in-{Guid.NewGuid():N}");
        var repoPath = Path.Combine(searchRoot, "repo");
        Directory.CreateDirectory(Path.Combine(repoPath, ".git"));
        try
        {
            var volume = WatchPath.Parse(repoPath).Volume!;
            var batch = new VolumeChangeBatch(volume,
                new[] { new VolumeChangeEntry(repoPath, VolumeEntryKind.MovedIn) });
            var svc = new RepositoryWatchService(
                new FakeVolumeChangeSource(new[] { batch }),
                trackedRoots: Array.Empty<string>(),
                searchRoots: new[] { searchRoot },
                debounce: TimeSpan.FromMilliseconds(10));

            var events = await DrainAsync(svc);

            Assert.That(events, Is.EqualTo(new[]
            {
                    new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Created),
                }));
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }

    [Test]
    public async Task MovedInPlainDirectoryWithoutGit_EmitsNoEvents()
    {
        var searchRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"watch-plain-{Guid.NewGuid():N}");
        var plainPath = Path.Combine(searchRoot, "folder-renamed");
        Directory.CreateDirectory(plainPath);
        try
        {
            var volume = WatchPath.Parse(plainPath).Volume!;
            var batch = new VolumeChangeBatch(volume,
                new[] { new VolumeChangeEntry(plainPath, VolumeEntryKind.MovedIn) });
            var svc = new RepositoryWatchService(
                new FakeVolumeChangeSource(new[] { batch }),
                trackedRoots: Array.Empty<string>(),
                searchRoots: new[] { searchRoot },
                debounce: TimeSpan.FromMilliseconds(10));

            var events = await DrainAsync(svc);

            Assert.That(events, Is.Empty);
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }

    [Test]
    public async Task MovedInWorktreeWithGitFile_EmitsCreated()
    {
        // A gitfile worktree or submodule has .git as a file, not a directory.
        var searchRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"watch-gitfile-{Guid.NewGuid():N}");
        var worktreePath = Path.Combine(searchRoot, "worktree");
        Directory.CreateDirectory(worktreePath);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, ".git"), "gitdir: C:/elsewhere/.git");
        try
        {
            var volume = WatchPath.Parse(worktreePath).Volume!;
            var batch = new VolumeChangeBatch(volume,
                new[] { new VolumeChangeEntry(worktreePath, VolumeEntryKind.MovedIn) });
            var svc = new RepositoryWatchService(
                new FakeVolumeChangeSource(new[] { batch }),
                trackedRoots: Array.Empty<string>(),
                searchRoots: new[] { searchRoot },
                debounce: TimeSpan.FromMilliseconds(10));

            var events = await DrainAsync(svc);

            Assert.That(events, Is.EqualTo(new[]
            {
                    new RepositoryChangeEvent(worktreePath, RepositoryChangeKind.Created),
                }));
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }

    [Test]
    public async Task MovedInRepositoryUnderIgnoredPath_EmitsNoEvents()
    {
        var searchRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"watch-ignored-{Guid.NewGuid():N}");
        var ignoredRoot = Path.Combine(searchRoot, "ignored");
        var repoPath = Path.Combine(ignoredRoot, "repo");
        Directory.CreateDirectory(Path.Combine(repoPath, ".git"));
        try
        {
            var volume = WatchPath.Parse(repoPath).Volume!;
            var batch = new VolumeChangeBatch(volume,
                new[] { new VolumeChangeEntry(repoPath, VolumeEntryKind.MovedIn) });
            var svc = new RepositoryWatchService(
                new FakeVolumeChangeSource(new[] { batch }),
                trackedRoots: Array.Empty<string>(),
                searchRoots: new[] { searchRoot },
                debounce: TimeSpan.FromMilliseconds(10),
                ignoredPaths: new[] { ignoredRoot });

            var events = await DrainAsync(svc);

            Assert.That(events, Is.Empty);
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }

    [Test]
    public async Task MovedInGitDirectoryUnderSearchRoot_EmitsCreatedForRepositoryRoot()
    {
        var searchRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"watch-marker-{Guid.NewGuid():N}");
        var repoPath = Path.Combine(searchRoot, "repo");
        var gitPath = Path.Combine(repoPath, ".git");
        Directory.CreateDirectory(gitPath);
        try
        {
            var volume = WatchPath.Parse(repoPath).Volume!;
            var batch = new VolumeChangeBatch(volume,
                new[] { new VolumeChangeEntry(gitPath, VolumeEntryKind.MovedIn) });
            var svc = new RepositoryWatchService(
                new FakeVolumeChangeSource(new[] { batch }),
                trackedRoots: Array.Empty<string>(),
                searchRoots: new[] { searchRoot },
                debounce: TimeSpan.FromMilliseconds(10));

            var events = await DrainAsync(svc);

            Assert.That(events, Is.EqualTo(new[]
            {
                    new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Created),
                }));
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }

    [Test]
    public async Task MovedInRepositoryUnderNonExistentIgnoredPath_EmitsNoEvents()
    {
        var searchRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"watch-nonexistent-ignored-{Guid.NewGuid():N}");
        var ignoredRoot = Path.Combine(searchRoot, "ignored-dir");
        var repoPath = Path.Combine(ignoredRoot, "repo");
        Directory.CreateDirectory(Path.Combine(repoPath, ".git"));
        try
        {
            var volume = WatchPath.Parse(repoPath).Volume!;
            var batch = new VolumeChangeBatch(volume,
                new[] { new VolumeChangeEntry(repoPath, VolumeEntryKind.MovedIn) });
            var svc = new RepositoryWatchService(
                new FakeVolumeChangeSource(new[] { batch }),
                trackedRoots: Array.Empty<string>(),
                searchRoots: new[] { searchRoot },
                debounce: TimeSpan.FromMilliseconds(10),
                ignoredPaths: new[] { ignoredRoot });

            var events = await DrainAsync(svc);

            Assert.That(events, Is.Empty);
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }
}
