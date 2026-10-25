using GitWizard;
using GitWizard.Watch;

namespace GitWizardTests;

// Watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryChangeFilter (case normalization, UNC paths, and POSIX path preservation).
public class RepositoryChangeFilterPathShapeTests
{
    static VolumeChangeEntry Modified(string fullPath) => new(fullPath, VolumeEntryKind.Modified);
    static VolumeChangeEntry Created(string fullPath) => new(fullPath, VolumeEntryKind.Created);
    static VolumeChangeEntry Deleted(string fullPath) => new(fullPath, VolumeEntryKind.Deleted);
    static VolumeChangeEntry MovedIn(string fullPath) => new(fullPath, VolumeEntryKind.MovedIn);

    [Test]
    public void Classify_WindowsModifiedEntryWithDifferentCasing_MapsToChanged()
    {
        const string repoRoot = @"C:\Repos\Repo-A";
        var filter = new RepositoryChangeFilter([repoRoot]);

        var result = filter.Classify([Modified(@"c:\repos\repo-a\index.lock")]);

        Assert.That(result.Changed, Is.EquivalentTo([repoRoot]));
    }

    [Test]
    public void Classify_PosixModifiedEntryWithDifferentCasing_IsIgnored()
    {
        var filter = new RepositoryChangeFilter(["/repos/repo-a"]);

        var result = filter.Classify([Modified("/repos/Repo-A/index.lock")]);

        Assert.That(result.Changed, Is.Empty);
    }

    [Test]
    public void Classify_WindowsGitDirectoryNameWithDifferentCasing_MapsToCreated()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\Repos"]);

        var result = filter.Classify([Created(@"c:\repos\new-repo\.GIT")]);

        Assert.That(result.Created, Is.EquivalentTo([@"c:\repos\new-repo"]));
    }

    [Test]
    public void Classify_PosixGitDirectoryNameWithDifferentCasing_IsIgnored()
    {
        var filter = new RepositoryChangeFilter([], ["/repos"]);

        var result = filter.Classify([Created("/repos/new-repo/.GIT")]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_UncModifiedEntryWithDifferentCasing_EmitsOriginalRoot()
    {
        const string repositoryRoot = @"\\Server\Share\Repo";
        var filter = new RepositoryChangeFilter([repositoryRoot]);

        var result = filter.Classify([Modified(@"\\server\share\repo\file.txt")]);

        Assert.That(result.Changed, Is.EquivalentTo([repositoryRoot]));
    }

    [Test]
    public void Classify_UncGitDirectoryWithDifferentCasing_MapsToCreated()
    {
        var filter = new RepositoryChangeFilter([], [@"\\Server\Share\Repos"]);

        var result = filter.Classify([Created(@"\\server\share\repos\NewRepo\.GIT")]);

        Assert.That(result.Created, Is.EquivalentTo([@"\\server\share\repos\NewRepo"]));
    }

    [Test]
    public void Classify_RootedWindowsModifiedEntryWithDifferentCasing_EmitsOriginalRoot()
    {
        const string repositoryRoot = @"\Repos\Repo";
        var filter = new RepositoryChangeFilter([repositoryRoot]);

        var result = filter.Classify([Modified(@"\repos\repo\file.txt")]);

        Assert.That(result.Changed, Is.EquivalentTo([repositoryRoot]));
    }

    [Test]
    public void Classify_RootedWindowsGitDirectoryWithDifferentCasing_MapsToCreated()
    {
        var filter = new RepositoryChangeFilter([], [@"\Repos"]);

        var result = filter.Classify([Created(@"\repos\NewRepo\.GIT")]);

        Assert.That(result.Created, Is.EquivalentTo([@"\repos\NewRepo"]));
    }

    [Test]
    public void Classify_DriveRootModifiedEntry_EmitsAbsoluteOriginalRoot()
    {
        const string repositoryRoot = @"C:\";
        var filter = new RepositoryChangeFilter([repositoryRoot]);

        var result = filter.Classify([Modified(@"c:\file.txt")]);

        Assert.That(result.Changed, Is.EquivalentTo([repositoryRoot]));
    }

    [Test]
    public void Classify_DriveRootDeletedEntry_EmitsAbsoluteOriginalRoot()
    {
        const string repositoryRoot = @"C:\";
        var filter = new RepositoryChangeFilter([repositoryRoot]);

        var result = filter.Classify([Deleted(@"c:\")]);

        Assert.That(result.Deleted, Is.EquivalentTo([repositoryRoot]));
    }

    [Test]
    public void Classify_PosixMovedInUnderSearchRoot_PreservesPath()
    {
        var filter = new RepositoryChangeFilter([], ["/repos"]);

        var result = filter.Classify([new VolumeChangeEntry("/repos/repo-renamed", VolumeEntryKind.MovedIn)]);

        Assert.That(result.MovedInCandidates, Is.EquivalentTo(["/repos/repo-renamed"]));
    }

    [Test]
    public void Classify_WindowsMovedInTrackedRootWithDifferentCasing_AddsMovedInCandidate()
    {
        const string repoRoot = @"C:\repos\repo-a";
        var filter = new RepositoryChangeFilter([repoRoot], [@"C:\repos"]);

        var result = filter.Classify([Deleted(repoRoot), MovedIn(@"C:\repos\Repo-A")]);

        Assert.That(result.Deleted, Is.EquivalentTo([repoRoot]));
        Assert.That(result.MovedInCandidates, Is.EquivalentTo([@"C:\repos\Repo-A"]));
        Assert.That(result.Changed, Is.Empty);
        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_WindowsMovedInTrackedRootOutsideSearchRootsWithDifferentCasing_AddsMovedInCandidate()
    {
        const string repoRoot = @"D:\repos\repo-a";
        var filter = new RepositoryChangeFilter([repoRoot]);

        var result = filter.Classify([MovedIn(@"D:\repos\Repo-A")]);

        Assert.That(result.MovedInCandidates, Is.EquivalentTo([@"D:\repos\Repo-A"]));
        Assert.That(result.Changed, Is.Empty);
    }

    [Test]
    public void Classify_PosixModifiedEntry_PreservesTrackedRootPath()
    {
        const string repoRoot = "/repos/repo-a";
        var filter = new RepositoryChangeFilter([repoRoot], ["/repos"]);

        var result = filter.Classify([Modified("/repos/repo-a/index.lock")]);

        Assert.That(result.Changed, Is.EquivalentTo([repoRoot]));
    }

    [Test]
    public void Classify_PosixGitCreated_PreservesParentPath()
    {
        var filter = new RepositoryChangeFilter([], ["/repos"]);

        var result = filter.Classify([Created("/repos/new-repo/.git")]);

        Assert.That(result.Created, Is.EquivalentTo(["/repos/new-repo"]));
    }

    [Test]
    public void Classify_PosixGitCreatedUnderFilesystemRoot_MapsToCreatedParent()
    {
        var filter = new RepositoryChangeFilter([], ["/"]);

        var result = filter.Classify([Created("/repos/new-repo/.git")]);

        Assert.That(result.Created, Is.EquivalentTo(["/repos/new-repo"]));
    }
}
