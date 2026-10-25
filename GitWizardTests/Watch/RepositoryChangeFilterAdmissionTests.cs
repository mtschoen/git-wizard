using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

// Watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryChangeFilter (temporary directories and ignored-path admission).
public class RepositoryChangeFilterAdmissionTests
{
    static VolumeChangeEntry Modified(string fullPath) => new(fullPath, VolumeEntryKind.Modified);
    static VolumeChangeEntry Created(string fullPath) => new(fullPath, VolumeEntryKind.Created);
    static VolumeChangeEntry Deleted(string fullPath) => new(fullPath, VolumeEntryKind.Deleted);

    [Test]
    public void Classify_GitCreatedUnderOtherProfilesTemp_IsIgnoredByDefault()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\"]);

        var result = filter.Classify([Created(@"C:\Users\example\AppData\Local\Temp\pytest-12602\repo\.git")]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_GitCreatedUnderOtherProfilesTemp_MapsToCreatedWhenIncluded()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\"], includeTempDirectories: true);

        var result = filter.Classify([Created(@"C:\Users\example\AppData\Local\Temp\pytest-12602\repo\.git")]);

        Assert.That(result.Created, Is.EquivalentTo([@"C:\Users\example\AppData\Local\Temp\pytest-12602\repo"]));
    }

    [Test]
    public void Classify_GitCreatedUnderCurrentProcessTemp_IsIgnoredByDefault()
    {
        // The search root is the filesystem root, not the temp directory itself: a search root
        // inside a temp directory opts that subtree back in (see the carve-out test below).
        var tempRepoGit = Path.Combine(TempDirectory.SystemTempPath, "gw-created-repo", ".git");
        var searchRoot = Path.GetPathRoot(TempDirectory.SystemTempPath)!;
        var filter = new RepositoryChangeFilter([], [searchRoot]);

        var result = filter.Classify([Created(tempRepoGit)]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_GitCreatedWhenSearchRootInsideTemp_StillMapsToCreated()
    {
        var tempRoot = TempDirectory.SystemTempPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoPath = Path.Combine(tempRoot, "gw-created-repo");
        var filter = new RepositoryChangeFilter([], [tempRoot]);


        var result = filter.Classify([Created(Path.Combine(repoPath, ".git"))]);

        Assert.That(result.Created, Is.EquivalentTo([repoPath]));
    }

    [Test]
    public void Classify_PosixGitCreatedUnderTmp_IsIgnoredByDefault()
    {
        var filter = new RepositoryChangeFilter([], ["/"]);

        var result = filter.Classify([Created("/tmp/scratch/repo/.git")]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_ModifiedEntryUnderTrackedTempRoot_IsIgnoredByDefault()
    {
        var trackedTempRepo = Path.Combine(TempDirectory.SystemTempPath, "gw-filter", "repo");
        var filter = new RepositoryChangeFilter([trackedTempRepo], [Path.GetPathRoot(trackedTempRepo)!]);

        var result = filter.Classify([Modified(Path.Combine(trackedTempRepo, "index.lock"))]);

        Assert.That(result.Changed, Is.Empty);
    }

    [Test]
    public void Classify_GitRecreatedUnderTrackedTempRoot_IsIgnoredByDefault()
    {
        var trackedTempRepo = Path.Combine(TempDirectory.SystemTempPath, "gw-filter", "repo");
        var filter = new RepositoryChangeFilter([trackedTempRepo], [Path.GetPathRoot(trackedTempRepo)!]);

        var result = filter.Classify([Created(Path.Combine(trackedTempRepo, ".git"))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Changed, Is.Empty);
            Assert.That(result.Created, Is.Empty);
        }
    }

    [Test]
    public void Classify_TrackedTempRoot_DeletedIsIgnoredByDefault()
    {
        var trackedTempRepo = Path.Combine(TempDirectory.SystemTempPath, "gw-filter", "repo");
        var filter = new RepositoryChangeFilter([trackedTempRepo], [Path.GetPathRoot(trackedTempRepo)!]);

        var result = filter.Classify([Deleted(trackedTempRepo)]);

        Assert.That(result.Deleted, Is.Empty);
    }

    [Test]
    public void Classify_TrackedTempRoot_EmitsChangedWhenIncludeTempDirectoriesTrue()
    {
        var trackedTempRepo = Path.Combine(TempDirectory.SystemTempPath, "gw-filter", "repo");
        var filter = new RepositoryChangeFilter([trackedTempRepo], [Path.GetPathRoot(trackedTempRepo)!], includeTempDirectories: true);

        var result = filter.Classify([Modified(Path.Combine(trackedTempRepo, "index.lock"))]);

        Assert.That(result.Changed, Is.EquivalentTo([trackedTempRepo]));
    }

    [Test]
    public void Classify_GitRecreatedUnderTrackedTempRoot_EmitsChangedWhenIncludeTempDirectoriesTrue()
    {
        var trackedTempRepo = Path.Combine(TempDirectory.SystemTempPath, "gw-filter", "repo");
        var filter = new RepositoryChangeFilter([trackedTempRepo], [Path.GetPathRoot(trackedTempRepo)!], includeTempDirectories: true);

        var result = filter.Classify([Created(Path.Combine(trackedTempRepo, ".git"))]);

        Assert.That(result.Changed, Is.EquivalentTo([trackedTempRepo]));
    }

    [Test]
    public void Classify_TrackedTempRoot_EmitsChangedWhenSearchRootInsideTemp()
    {
        var tempRoot = Path.Combine(TempDirectory.SystemTempPath, "gw-filter");
        var trackedTempRepo = Path.Combine(tempRoot, "repo");
        var filter = new RepositoryChangeFilter([trackedTempRepo], [tempRoot]);

        var result = filter.Classify([Modified(Path.Combine(trackedTempRepo, "index.lock"))]);

        Assert.That(result.Changed, Is.EquivalentTo([trackedTempRepo]));
    }

    [Test]
    public void Classify_GitCreatedUnderIgnoredPath_IsIgnoredEvenWhenTempIncluded()
    {
        var ignoredPath = Path.Combine(TempDirectory.SystemTempPath, "gw-filter", "ignored");
        var searchRoot = Path.GetPathRoot(ignoredPath)!;
        var repoGit = Path.Combine(ignoredPath, "repo", ".git");
        var filter = new RepositoryChangeFilter([], [searchRoot], [ignoredPath], includeTempDirectories: true);

        var result = filter.Classify([Created(repoGit)]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_ModifiedEntryUnderIgnoredPath_IsIgnored()
    {
        const string trackedRepo = @"C:\repos\ignored-repo";
        var filter = new RepositoryChangeFilter([trackedRepo], [@"C:\repos"], [trackedRepo]);

        var result = filter.Classify([Modified(Path.Combine(trackedRepo, "file.txt"))]);

        Assert.That(result.Changed, Is.Empty);
    }

    [Test]
    public void Classify_TrackedRootUnderIgnoredPath_DeletedIsIgnored()
    {
        const string trackedRepo = @"C:\repos\ignored-repo";
        var filter = new RepositoryChangeFilter([trackedRepo], [@"C:\repos"], [trackedRepo]);

        var result = filter.Classify([Deleted(trackedRepo)]);

        Assert.That(result.Deleted, Is.Empty);
    }

    [Test]
    public void Classify_PosixIgnoredPath_IgnoresChanges()
    {
        const string trackedRepo = "/repos/ignored-repo";
        var filter = new RepositoryChangeFilter([trackedRepo], ["/repos"], [trackedRepo]);

        var result = filter.Classify([Modified("/repos/ignored-repo/index.lock")]);

        Assert.That(result.Changed, Is.Empty);
    }
}
