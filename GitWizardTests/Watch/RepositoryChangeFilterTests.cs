using GitWizard;
using GitWizard.Watch;

namespace GitWizardTests;

// Watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryChangeFilter (core change classification and candidate tracking).
public class RepositoryChangeFilterTests
{
    const string RepoRoot = @"C:\repos\repo-a";
    static VolumeChangeEntry Modified(string fullPath) => new(fullPath, VolumeEntryKind.Modified);
    static VolumeChangeEntry Created(string fullPath) => new(fullPath, VolumeEntryKind.Created);
    static VolumeChangeEntry Deleted(string fullPath) => new(fullPath, VolumeEntryKind.Deleted);
    static VolumeChangeEntry MovedIn(string fullPath) => new(fullPath, VolumeEntryKind.MovedIn);

    [Test]
    public void Classify_MovedInUnderTrackedRoot_MapsToChanged()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([MovedIn(Path.Combine(RepoRoot, "src-renamed"))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Changed, Is.EquivalentTo([RepoRoot]));
            Assert.That(result.MovedInCandidates, Is.Empty);
            Assert.That(result.Created, Is.Empty);
        }
    }

    [Test]
    public void Classify_MovedInUntrackedDirectoryUnderSearchRoot_AddsMovedInCandidate()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\repos"]);

        var result = filter.Classify([MovedIn(@"C:\repos\repo-renamed")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MovedInCandidates, Is.EquivalentTo([@"C:\repos\repo-renamed"]));
            Assert.That(result.Changed, Is.Empty);
            Assert.That(result.Created, Is.Empty);
            Assert.That(result.Deleted, Is.Empty);
        }
    }

    [Test]
    public void Classify_MovedInOutsideSearchRoots_IsIgnored()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\repos"]);

        var result = filter.Classify([MovedIn(@"C:\elsewhere\repo")]);

        Assert.That(result.MovedInCandidates, Is.Empty);
    }

    [Test]
    public void Classify_MovedInWithNoSearchRootsConfigured_IsIgnored()
    {
        var filter = new RepositoryChangeFilter([RepoRoot]);

        var result = filter.Classify([MovedIn(@"C:\repos\repo-renamed")]);

        Assert.That(result.MovedInCandidates, Is.Empty);
    }

    [Test]
    public void Classify_ModifiedEntryUnderTrackedRoot_MapsToChanged()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Modified(Path.Combine(RepoRoot, "index.lock"))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Changed, Is.EquivalentTo([RepoRoot]));
            Assert.That(result.Created, Is.Empty);
            Assert.That(result.Deleted, Is.Empty);
        }
    }

    [Test]
    public void Classify_FileCreatedUnderTrackedRoot_MapsToChanged()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Created(Path.Combine(RepoRoot, "test.txt"))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Changed, Is.EquivalentTo([RepoRoot]));
            Assert.That(result.Created, Is.Empty);
            Assert.That(result.Deleted, Is.Empty);
        }
    }

    [Test]
    public void Classify_FileDeletedUnderTrackedRoot_MapsToChanged()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Deleted(Path.Combine(RepoRoot, "test.txt"))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Changed, Is.EquivalentTo([RepoRoot]));
            Assert.That(result.Created, Is.Empty);
            Assert.That(result.Deleted, Is.Empty);
        }
    }

    [Test]
    public void Classify_GitCreatedDirectlyUnderSearchRoot_MapsToCreatedParent()
    {
        const string searchRoot = @"C:\repos";
        const string newRepoGitPath = @"C:\repos\new-repo\.git";
        var filter = new RepositoryChangeFilter([], [searchRoot]);

        var result = filter.Classify([Created(newRepoGitPath)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Created, Is.EquivalentTo([@"C:\repos\new-repo"]));
            Assert.That(result.Changed, Is.Empty);
            Assert.That(result.Deleted, Is.Empty);
        }
    }

    [Test]
    public void Classify_GitCreatedNestedUnderSearchRoot_MapsToCreatedParent()
    {
        const string searchRoot = @"C:\repos";
        const string newRepoGitPath = @"C:\repos\vendor\deep\new-repo\.git";
        var filter = new RepositoryChangeFilter([], [searchRoot]);

        var result = filter.Classify([Created(newRepoGitPath)]);

        Assert.That(result.Created, Is.EquivalentTo([@"C:\repos\vendor\deep\new-repo"]));
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
        const string searchRoot = @"C:\repos";
        var filter = new RepositoryChangeFilter([], [searchRoot]);

        var result = filter.Classify([Created(Path.Combine(searchRoot, "new-repo", "src"))]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_TrackedRootGitDirectoryDeleted_MapsToDeletedRoot()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Deleted(Path.Combine(RepoRoot, ".git"))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Deleted, Is.EquivalentTo([RepoRoot]));
            Assert.That(result.Changed, Is.Empty);
            Assert.That(result.Created, Is.Empty);
        }
    }

    [Test]
    public void Classify_TrackedRootItselfDeleted_MapsToDeletedRoot()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Deleted(RepoRoot)]);

        Assert.That(result.Deleted, Is.EquivalentTo([RepoRoot]));
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
        const string searchRoot = @"C:\repos";
        const string trackedRoot = @"C:\repos\existing-repo";
        const string deletedRoot = @"C:\repos\gone-repo";
        const string newRoot = @"C:\repos\new-repo";
        var filter = new RepositoryChangeFilter([trackedRoot, deletedRoot], [searchRoot]);

        var result = filter.Classify(
        [
            Modified(@"C:\repos\existing-repo\index.lock"),
                Created(@"C:\repos\new-repo\.git"),
                Deleted(@"C:\repos\gone-repo\.git"),
            ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Changed, Is.EquivalentTo([trackedRoot]));
            Assert.That(result.Created, Is.EquivalentTo([newRoot]));
            Assert.That(result.Deleted, Is.EquivalentTo([deletedRoot]));
        }
    }

    [Test]
    public void Classify_MovedInGitDirectoryUnderSearchRoot_AddsParentAsMovedInCandidate()
    {
        const string searchRoot = @"C:\repos";
        var filter = new RepositoryChangeFilter([], [searchRoot]);

        var result = filter.Classify([MovedIn(@"C:\repos\new-repo\.git")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MovedInCandidates, Is.EquivalentTo([@"C:\repos\new-repo"]));
            Assert.That(result.Created, Is.Empty);
            Assert.That(result.Changed, Is.Empty);
        }
    }

    [Test]
    public void ClassifyEntry_ReturnsSingleClassificationOrNull()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var modifiedResult = filter.ClassifyEntry(Modified(Path.Combine(RepoRoot, "file.txt")));
        Assert.That(modifiedResult, Is.EqualTo((RepositoryChangeKind.Changed, RepoRoot)));

        var createdResult = filter.ClassifyEntry(Created(@"C:\repos\fresh\.git"));
        Assert.That(createdResult, Is.EqualTo((RepositoryChangeKind.Created, @"C:\repos\fresh")));

        var deletedResult = filter.ClassifyEntry(Deleted(Path.Combine(RepoRoot, ".git")));
        Assert.That(deletedResult, Is.EqualTo((RepositoryChangeKind.Deleted, RepoRoot)));

        var unrelatedResult = filter.ClassifyEntry(Modified(@"C:\unrelated\noise.txt"));
        Assert.That(unrelatedResult, Is.Null);
    }

    [Test]
    public void Classify_MovedInGitDirectoryUnderTrackedRoot_MapsToChanged()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([MovedIn(Path.Combine(RepoRoot, ".git"))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Changed, Is.EquivalentTo([RepoRoot]));
            Assert.That(result.MovedInCandidates, Is.Empty);
            Assert.That(result.Created, Is.Empty);
            Assert.That(result.Deleted, Is.Empty);
        }
    }
}
