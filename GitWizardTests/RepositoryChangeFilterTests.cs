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

    [Test]
    public void AddRepositoryRoot_SubsequentModifiedUnderAddedRoot_MapsToChanged()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\repos"]);
        var created = filter.Classify([Created(@"C:\repos\new-repo\.git")]);
        Assert.That(created.Created, Is.EquivalentTo(new[] { @"C:\repos\new-repo" }));

        filter.AddRepositoryRoot(@"C:\repos\new-repo");

        var result = filter.Classify([Modified(@"C:\repos\new-repo\index.lock")]);
        Assert.That(result.Changed, Is.EquivalentTo(new[] { @"C:\repos\new-repo" }));
        Assert.That(result.Created, Is.Empty);
        Assert.That(result.Deleted, Is.Empty);
    }

    [Test]
    public void AddRepositoryRoot_NestedUnderExistingRoot_LongestMatchWins()
    {
        var filter = new RepositoryChangeFilter([@"C:\repos\outer"], [@"C:\repos"]);

        filter.AddRepositoryRoot(@"C:\repos\outer\inner");

        var result = filter.Classify([Modified(@"C:\repos\outer\inner\index.lock")]);
        Assert.That(result.Changed, Is.EquivalentTo(new[] { @"C:\repos\outer\inner" }));
    }

    [Test]
    public void AddRepositoryRoot_DuplicateWithDifferentCasing_DoesNotTrackTwice()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\repos"]);

        filter.AddRepositoryRoot(@"C:\repos\New-Repo");
        filter.AddRepositoryRoot(@"C:\repos\new-repo");

        var recreated = filter.Classify([Created(@"C:\repos\NEW-REPO\.git")]);
        Assert.That(recreated.Created, Is.Empty);
        Assert.That(recreated.Changed, Is.EquivalentTo(new[] { @"C:\repos\New-Repo" }));
    }

    [Test]
    public void AddRepositoryRoot_PosixRoot_PreservesPathShape()
    {
        var filter = new RepositoryChangeFilter([], ["/repos"]);

        filter.AddRepositoryRoot("/repos/new-repo");

        var result = filter.Classify([Modified("/repos/new-repo/index.lock")]);
        Assert.That(result.Changed, Is.EquivalentTo(new[] { "/repos/new-repo" }));
    }

    [Test]
    public void RemoveRepositoryRoot_SubsequentChanges_IgnoredUntilGitRecreated()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        filter.RemoveRepositoryRoot(RepoRoot);

        var afterRemoval = filter.Classify([Modified(Path.Combine(RepoRoot, "index.lock"))]);
        Assert.That(afterRemoval.Changed, Is.Empty);

        var recreated = filter.Classify([Created(Path.Combine(RepoRoot, ".git"))]);
        Assert.That(recreated.Created, Is.EquivalentTo(new[] { RepoRoot }));
        Assert.That(recreated.Changed, Is.Empty);
    }

    [Test]
    public void RemoveRepositoryRoot_UnknownRoot_IsANoOp()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        filter.RemoveRepositoryRoot(@"C:\repos\never-tracked");

        var result = filter.Classify([Modified(Path.Combine(RepoRoot, "index.lock"))]);
        Assert.That(result.Changed, Is.EquivalentTo(new[] { RepoRoot }));
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
    public void AddRepositoryRoot_ManyAdoptions_MaintainsDescendingOrderAndClassifiesCorrectly()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\repos"]);

        // Add 5000 roots of varying lengths in arbitrary order
        for (var index = 0; index < 5000; index++)
        {
            var depth = index % 37;
            filter.AddRepositoryRoot($@"C:\repos\dir_{depth}\project_{index:D4}\sub_{index % 11}");
        }

        // Add nested root
        var targetRoot = $@"C:\repos\dir_{42 % 37}\project_0042\sub_{42 % 11}";
        filter.AddRepositoryRoot($@"{targetRoot}\nested_component");

        // Verify nested root matches first (longest match)
        var nestedResult = filter.ClassifyEntry(Modified($@"{targetRoot}\nested_component\file.txt"));
        Assert.That(nestedResult, Is.EqualTo((RepositoryChangeKind.Changed, $@"{targetRoot}\nested_component")));

        // Verify outer root matches when outside nested
        var outerResult = filter.ClassifyEntry(Modified($@"{targetRoot}\file.txt"));
        Assert.That(outerResult, Is.EqualTo((RepositoryChangeKind.Changed, targetRoot)));
    }

    [Test]
    public void RemoveRepositoryRoot_AllowsReAddingAfterRemoval()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\repos"]);

        filter.AddRepositoryRoot(@"C:\repos\temp");
        Assert.That(filter.ClassifyEntry(Modified(@"C:\repos\temp\file.txt")),
            Is.EqualTo((RepositoryChangeKind.Changed, @"C:\repos\temp")));

        filter.RemoveRepositoryRoot(@"C:\repos\temp");
        Assert.That(filter.ClassifyEntry(Modified(@"C:\repos\temp\file.txt")), Is.Null);

        filter.AddRepositoryRoot(@"C:\repos\temp");
        Assert.That(filter.ClassifyEntry(Modified(@"C:\repos\temp\file.txt")),
            Is.EqualTo((RepositoryChangeKind.Changed, @"C:\repos\temp")));
    }
}
