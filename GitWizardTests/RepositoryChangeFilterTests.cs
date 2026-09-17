using GitWizard;
using GitWizard.Watch;

namespace GitWizardTests;

public partial class RepositoryChangeFilterTests
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

        Assert.That(result.Changed, Is.EquivalentTo(new[] { RepoRoot }));
        Assert.That(result.MovedInCandidates, Is.Empty);
        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_MovedInUntrackedDirectoryUnderSearchRoot_AddsMovedInCandidate()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\repos"]);

        var result = filter.Classify([MovedIn(@"C:\repos\repo-renamed")]);

        Assert.That(result.MovedInCandidates, Is.EquivalentTo(new[] { @"C:\repos\repo-renamed" }));
        Assert.That(result.Changed, Is.Empty);
        Assert.That(result.Created, Is.Empty);
        Assert.That(result.Deleted, Is.Empty);
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
    public void Classify_GitCreatedUnderOtherProfilesTemp_IsIgnoredByDefault()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\"]);

        var result = filter.Classify([Created(@"C:\Users\gitea-runner\AppData\Local\Temp\pytest-12602\repo\.git")]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_GitCreatedUnderOtherProfilesTemp_MapsToCreatedWhenIncluded()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\"], includeTempDirectories: true);

        var result = filter.Classify([Created(@"C:\Users\gitea-runner\AppData\Local\Temp\pytest-12602\repo\.git")]);

        Assert.That(result.Created, Is.EquivalentTo(new[] { @"C:\Users\gitea-runner\AppData\Local\Temp\pytest-12602\repo" }));
    }

    [Test]
    public void Classify_GitCreatedUnderCurrentProcessTemp_IsIgnoredByDefault()
    {
        // The search root is the filesystem root, not the temp directory itself: a search root
        // inside a temp directory opts that subtree back in (see the carve-out test below).
        var tempRepoGit = Path.Combine(Path.GetTempPath(), "gw-created-repo", ".git");
        var searchRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var filter = new RepositoryChangeFilter([], [searchRoot]);

        var result = filter.Classify([Created(tempRepoGit)]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_GitCreatedWhenSearchRootInsideTemp_StillMapsToCreated()
    {
        var tempRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoPath = Path.Combine(tempRoot, "gw-created-repo");
        var filter = new RepositoryChangeFilter([], [tempRoot]);

        var result = filter.Classify([Created(Path.Combine(repoPath, ".git"))]);

        Assert.That(result.Created, Is.EquivalentTo(new[] { repoPath }));
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
        const string trackedTempRepo = @"C:\Users\gitea-runner\AppData\Local\Temp\pytest-12602\repo";
        var filter = new RepositoryChangeFilter([trackedTempRepo], [@"C:\"]);

        var result = filter.Classify([Modified(Path.Combine(trackedTempRepo, "index.lock"))]);

        Assert.That(result.Changed, Is.Empty);
    }

    [Test]
    public void Classify_GitRecreatedUnderTrackedTempRoot_IsIgnoredByDefault()
    {
        const string trackedTempRepo = @"C:\Users\gitea-runner\AppData\Local\Temp\pytest-12602\repo";
        var filter = new RepositoryChangeFilter([trackedTempRepo], [@"C:\"]);

        var result = filter.Classify([Created(Path.Combine(trackedTempRepo, ".git"))]);

        Assert.That(result.Changed, Is.Empty);
        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_TrackedTempRoot_DeletedIsIgnoredByDefault()
    {
        const string trackedTempRepo = @"C:\Users\gitea-runner\AppData\Local\Temp\pytest-12602\repo";
        var filter = new RepositoryChangeFilter([trackedTempRepo], [@"C:\"]);

        var result = filter.Classify([Deleted(trackedTempRepo)]);

        Assert.That(result.Deleted, Is.Empty);
    }

    [Test]
    public void Classify_MovedInGitDirectoryUnderSearchRoot_AddsParentAsMovedInCandidate()
    {
        var searchRoot = @"C:\repos";
        var filter = new RepositoryChangeFilter([], [searchRoot]);

        var result = filter.Classify([MovedIn(@"C:\repos\new-repo\.git")]);

        Assert.That(result.MovedInCandidates, Is.EquivalentTo(new[] { @"C:\repos\new-repo" }));
        Assert.That(result.Created, Is.Empty);
        Assert.That(result.Changed, Is.Empty);
    }

    [Test]
    public void Classify_TrackedTempRoot_EmitsChangedWhenIncludeTempDirectoriesTrue()
    {
        const string trackedTempRepo = @"C:\Users\gitea-runner\AppData\Local\Temp\pytest-12602\repo";
        var filter = new RepositoryChangeFilter([trackedTempRepo], [@"C:\"], includeTempDirectories: true);

        var result = filter.Classify([Modified(Path.Combine(trackedTempRepo, "index.lock"))]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { trackedTempRepo }));
    }

    [Test]
    public void Classify_GitRecreatedUnderTrackedTempRoot_EmitsChangedWhenIncludeTempDirectoriesTrue()
    {
        const string trackedTempRepo = @"C:\Users\gitea-runner\AppData\Local\Temp\pytest-12602\repo";
        var filter = new RepositoryChangeFilter([trackedTempRepo], [@"C:\"], includeTempDirectories: true);

        var result = filter.Classify([Created(Path.Combine(trackedTempRepo, ".git"))]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { trackedTempRepo }));
    }

    [Test]
    public void Classify_TrackedTempRoot_EmitsChangedWhenSearchRootInsideTemp()
    {
        const string tempRoot = @"C:\Users\gitea-runner\AppData\Local\Temp\pytest-12602";
        const string trackedTempRepo = @"C:\Users\gitea-runner\AppData\Local\Temp\pytest-12602\repo";
        var filter = new RepositoryChangeFilter([trackedTempRepo], [tempRoot]);

        var result = filter.Classify([Modified(Path.Combine(trackedTempRepo, "index.lock"))]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { trackedTempRepo }));
    }

    [Test]
    public void Classify_GitCreatedUnderIgnoredPath_IsIgnoredEvenWhenTempIncluded()
    {
        const string searchRoot = @"C:\";
        const string ignoredPath = @"C:\Users\gitea-runner\AppData\Local\Temp\ci-runner";
        const string repoGit = @"C:\Users\gitea-runner\AppData\Local\Temp\ci-runner\repo\.git";
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

    [Test]
    public void Classify_MovedInGitDirectoryUnderTrackedRoot_MapsToChanged()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([MovedIn(Path.Combine(RepoRoot, ".git"))]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { RepoRoot }));
        Assert.That(result.MovedInCandidates, Is.Empty);
        Assert.That(result.Created, Is.Empty);
        Assert.That(result.Deleted, Is.Empty);
    }
}
