using GitWizard.Watch;
using GitWizardTests.TestSupport;
using static GitWizardTests.TestSupport.WatchEventDrain;

namespace GitWizardTests.Watch;

// Watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryWatchService (moved-in directory candidate detection and adoption).
[TestFixture]
public class RepositoryWatchServiceMovedInTests
{
    static Task<List<RepositoryChangeEvent>> RunMovedInAsync(
        string searchRoot,
        VolumeChangeBatch[] batches,
        string[]? trackedRoots = null,
        string[]? ignoredPaths = null)
    {
        var service = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(batches),
            trackedRoots: trackedRoots ?? [],
            searchRoots: [searchRoot],
            debounce: TimeSpan.FromMilliseconds(10),
            ignoredPaths: ignoredPaths);
        return DrainAsync(service);
    }

    [Test]
    public async Task MovedInTrackedRootRenameWithinSearchRoot_EmitsDeletedThenCreated()
    {
        using var temporaryRoot = new TempDirectory("watch-rename-");
        var searchRoot = temporaryRoot.Path;
        var oldPath = Path.Combine(searchRoot, "repo");
        var newPath = Path.Combine(searchRoot, "repo-renamed");
        Directory.CreateDirectory(Path.Combine(newPath, ".git"));
        var volume = WatchPath.Parse(newPath).Volume!;
        var batch = new VolumeChangeBatch(volume, [
                new VolumeChangeEntry(oldPath, VolumeEntryKind.Deleted),
                new VolumeChangeEntry(newPath, VolumeEntryKind.MovedIn),
            ]);

        var events = await RunMovedInAsync(searchRoot, [batch], trackedRoots: [oldPath]);

        // Order matters: the probe-emitted Created lands after the window's Deleted events so
        // the UI's pending-delete rename correlation sees the old root first.
        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(oldPath, RepositoryChangeKind.Deleted),
                new RepositoryChangeEvent(newPath, RepositoryChangeKind.Created),
            ]));
    }

    [Test]
    public async Task MovedInRepositoryFromOutsideSearchRoots_EmitsCreated()
    {
        // The service never sees the rename's previous path; a move-in from outside every search
        // root is the same MovedIn batch with no matching Deleted.
        using var temporaryRoot = new TempDirectory("watch-move-in-");
        var searchRoot = temporaryRoot.Path;
        var repoPath = Path.Combine(searchRoot, "repo");
        Directory.CreateDirectory(Path.Combine(repoPath, ".git"));
        var volume = WatchPath.Parse(repoPath).Volume!;
        var batch = new VolumeChangeBatch(volume,
            [new VolumeChangeEntry(repoPath, VolumeEntryKind.MovedIn)]);

        var events = await RunMovedInAsync(searchRoot, [batch]);

        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Created),
            ]));
    }

    [Test]
    public async Task MovedInPlainDirectoryWithoutGit_EmitsNoEvents()
    {
        using var temporaryRoot = new TempDirectory("watch-plain-");
        var searchRoot = temporaryRoot.Path;
        var plainPath = Path.Combine(searchRoot, "folder-renamed");
        Directory.CreateDirectory(plainPath);
        var volume = WatchPath.Parse(plainPath).Volume!;
        var batch = new VolumeChangeBatch(volume,
            [new VolumeChangeEntry(plainPath, VolumeEntryKind.MovedIn)]);

        var events = await RunMovedInAsync(searchRoot, [batch]);

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task MovedInWorktreeWithGitFile_EmitsCreated()
    {
        // A gitfile worktree or submodule has .git as a file, not a directory.
        using var temporaryRoot = new TempDirectory("watch-gitfile-");
        var searchRoot = temporaryRoot.Path;
        var worktreePath = Path.Combine(searchRoot, "worktree");
        Directory.CreateDirectory(worktreePath);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, ".git"), "gitdir: C:/elsewhere/.git");
        var volume = WatchPath.Parse(worktreePath).Volume!;
        var batch = new VolumeChangeBatch(volume,
            [new VolumeChangeEntry(worktreePath, VolumeEntryKind.MovedIn)]);

        var events = await RunMovedInAsync(searchRoot, [batch]);

        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(worktreePath, RepositoryChangeKind.Created),
            ]));
    }

    [Test]
    public async Task MovedInRepositoryUnderIgnoredPath_EmitsNoEvents()
    {
        using var temporaryRoot = new TempDirectory("watch-ignored-");
        var searchRoot = temporaryRoot.Path;
        var ignoredRoot = Path.Combine(searchRoot, "ignored");
        var repoPath = Path.Combine(ignoredRoot, "repo");
        Directory.CreateDirectory(Path.Combine(repoPath, ".git"));
        var volume = WatchPath.Parse(repoPath).Volume!;
        var batch = new VolumeChangeBatch(volume,
            [new VolumeChangeEntry(repoPath, VolumeEntryKind.MovedIn)]);

        var events = await RunMovedInAsync(searchRoot, [batch], ignoredPaths: [ignoredRoot]);

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task MovedInGitDirectoryUnderSearchRoot_EmitsCreatedForRepositoryRoot()
    {
        using var temporaryRoot = new TempDirectory("watch-marker-");
        var searchRoot = temporaryRoot.Path;
        var repoPath = Path.Combine(searchRoot, "repo");
        var gitPath = Path.Combine(repoPath, ".git");
        Directory.CreateDirectory(gitPath);
        var volume = WatchPath.Parse(repoPath).Volume!;
        var batch = new VolumeChangeBatch(volume,
            [new VolumeChangeEntry(gitPath, VolumeEntryKind.MovedIn)]);

        var events = await RunMovedInAsync(searchRoot, [batch]);

        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Created),
            ]));
    }
}
