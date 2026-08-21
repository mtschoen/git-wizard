using GitWizard;
using GitWizard.Watch;

namespace GitWizardTests;

public partial class RepositoryChangeFilterTests
{
    const string RepoRoot = @"C:\repos\repo-a";

    static VolumeChangeEntry Modified(string fullPath) => new(fullPath, VolumeEntryKind.Modified);
    static VolumeChangeEntry Created(string fullPath) => new(fullPath, VolumeEntryKind.Created);
    static VolumeChangeEntry Deleted(string fullPath) => new(fullPath, VolumeEntryKind.Deleted);

    [Test]
    public void Classify_ModifiedEntryUnderTrackedRoot_MapsToChanged()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Modified(Path.Combine(RepoRoot, "index.lock"))]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { RepoRoot }));
        Assert.That(result.Created, Is.Empty);
        Assert.That(result.Deleted, Is.Empty);
    }

    [Test]
    public void Classify_FileCreatedUnderTrackedRoot_MapsToChanged()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Created(Path.Combine(RepoRoot, "test.txt"))]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { RepoRoot }));
        Assert.That(result.Created, Is.Empty);
        Assert.That(result.Deleted, Is.Empty);
    }

    [Test]
    public void Classify_FileDeletedUnderTrackedRoot_MapsToChanged()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Deleted(Path.Combine(RepoRoot, "test.txt"))]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { RepoRoot }));
        Assert.That(result.Created, Is.Empty);
        Assert.That(result.Deleted, Is.Empty);
    }

    [Test]
    public void Classify_PosixModifiedEntry_PreservesTrackedRootPath()
    {
        const string repoRoot = "/repos/repo-a";
        var filter = new RepositoryChangeFilter([repoRoot], ["/repos"]);

        var result = filter.Classify([Modified("/repos/repo-a/index.lock")]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { repoRoot }));
    }

    [Test]
    public void Classify_GitCreatedDirectlyUnderSearchRoot_MapsToCreatedParent()
    {
        var searchRoot = @"C:\repos";
        var newRepoGitPath = @"C:\repos\new-repo\.git";
        var filter = new RepositoryChangeFilter([], [searchRoot]);

        var result = filter.Classify([Created(newRepoGitPath)]);

        Assert.That(result.Created, Is.EquivalentTo(new[] { @"C:\repos\new-repo" }));
        Assert.That(result.Changed, Is.Empty);
        Assert.That(result.Deleted, Is.Empty);
    }

    [Test]
    public void Classify_GitCreatedNestedUnderSearchRoot_MapsToCreatedParent()
    {
        var searchRoot = @"C:\repos";
        var newRepoGitPath = @"C:\repos\vendor\deep\new-repo\.git";
        var filter = new RepositoryChangeFilter([], [searchRoot]);

        var result = filter.Classify([Created(newRepoGitPath)]);

        Assert.That(result.Created, Is.EquivalentTo(new[] { @"C:\repos\vendor\deep\new-repo" }));
    }

    [Test]
    public void Classify_PosixGitCreated_PreservesParentPath()
    {
        var filter = new RepositoryChangeFilter([], ["/repos"]);

        var result = filter.Classify([Created("/repos/new-repo/.git")]);

        Assert.That(result.Created, Is.EquivalentTo(new[] { "/repos/new-repo" }));
    }

    [Test]
    public void Classify_PosixGitCreatedUnderFilesystemRoot_MapsToCreatedParent()
    {
        var filter = new RepositoryChangeFilter([], ["/"]);

        var result = filter.Classify([Created("/repos/new-repo/.git")]);

        Assert.That(result.Created, Is.EquivalentTo(new[] { "/repos/new-repo" }));
    }

    [Test]
    public void Classify_GitCreatedOutsideAnySearchRoot_IsIgnored()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\repos"]);

        var result = filter.Classify([Created(Path.Combine(@"C:\elsewhere\new-repo", ".git"))]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_GitCreatedWithNoSearchRootsConfigured_IsIgnored()
    {
        var filter = new RepositoryChangeFilter([RepoRoot]);

        var result = filter.Classify([Created(Path.Combine(@"C:\repos\new-repo", ".git"))]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_GitCreatedWithNoDirectorySeparator_IsIgnored()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\repos"]);

        var result = filter.Classify([Created(".git")]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_NonGitDirectoryCreated_IsIgnored()
    {
        var searchRoot = @"C:\repos";
        var filter = new RepositoryChangeFilter([], [searchRoot]);

        var result = filter.Classify([Created(Path.Combine(searchRoot, "new-repo", "src"))]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_TrackedRootGitDirectoryDeleted_MapsToDeletedRoot()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Deleted(Path.Combine(RepoRoot, ".git"))]);

        Assert.That(result.Deleted, Is.EquivalentTo(new[] { RepoRoot }));
        Assert.That(result.Changed, Is.Empty);
        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_TrackedRootItselfDeleted_MapsToDeletedRoot()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Deleted(RepoRoot)]);

        Assert.That(result.Deleted, Is.EquivalentTo(new[] { RepoRoot }));
    }

    [Test]
    public void Classify_UnrelatedPathDeleted_IsIgnored()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Deleted(@"C:\repos\unrelated\.git")]);

        Assert.That(result.Deleted, Is.Empty);
    }

    [Test]
    public void Classify_MixedBatch_PopulatesAllThreeCollectionsIndependently()
    {
        var searchRoot = @"C:\repos";
        var trackedRoot = @"C:\repos\existing-repo";
        var deletedRoot = @"C:\repos\gone-repo";
        var newRoot = @"C:\repos\new-repo";
        var filter = new RepositoryChangeFilter([trackedRoot, deletedRoot], [searchRoot]);

        var result = filter.Classify(
        [
            Modified(@"C:\repos\existing-repo\index.lock"),
            Created(@"C:\repos\new-repo\.git"),
            Deleted(@"C:\repos\gone-repo\.git"),
        ]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { trackedRoot }));
        Assert.That(result.Created, Is.EquivalentTo(new[] { newRoot }));
        Assert.That(result.Deleted, Is.EquivalentTo(new[] { deletedRoot }));
    }
}
