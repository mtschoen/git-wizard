using GitWizard;
using GitWizard.Watch;
using MFTLib;

namespace GitWizardTests;

public class RepositoryChangeFilterTests
{
    const string RepoRoot = @"C:\repos\repo-a";
    const ulong RepoRootRecord = 100;

    static ScanRecord Directory(ulong recordNumber, ulong parentRecordNumber, string path) =>
        new(recordNumber, parentRecordNumber, 0, 0, (uint)FileAttributes.Directory, true,
            Path.GetFileName(path), path);

    static ScanRecord File(ulong recordNumber, ulong parentRecordNumber, string path) =>
        new(recordNumber, parentRecordNumber, 0, 0, 0, false, Path.GetFileName(path), path);

    static UsnJournalEntry FileEntry(ulong recordNumber, ulong parentRecordNumber, string name) =>
        UsnJournalEntry.Create(recordNumber, parentRecordNumber, 0, DateTime.UtcNow,
            UsnReason.DataExtend, FileAttributes.Normal, name);

    static UsnJournalEntry DirectoryEntry(ulong recordNumber, ulong parentRecordNumber, string name) =>
        UsnJournalEntry.Create(recordNumber, parentRecordNumber, 0, DateTime.UtcNow,
            UsnReason.FileCreate, FileAttributes.Directory, name);

    [Test]
    public void Filter_FileUnderRepoRoot_MapsToRoot()
    {
        var scanRecords = new[] { Directory(RepoRootRecord, 1, RepoRoot) };
        var filter = new RepositoryChangeFilter(scanRecords, [RepoRoot]);

        var affected = filter.Filter([FileEntry(200, RepoRootRecord, "index.lock")]);

        Assert.That(affected, Is.EquivalentTo(new[] { RepoRoot }));
    }

    [Test]
    public void Filter_FileUnderNestedSubdirectory_MapsToRoot()
    {
        var subdirectoryPath = Path.Combine(RepoRoot, ".git");
        var scanRecords = new[]
        {
            Directory(RepoRootRecord, 1, RepoRoot),
            Directory(101, RepoRootRecord, subdirectoryPath),
        };
        var filter = new RepositoryChangeFilter(scanRecords, [RepoRoot]);

        var affected = filter.Filter([FileEntry(201, 101, "HEAD")]);

        Assert.That(affected, Is.EquivalentTo(new[] { RepoRoot }));
    }

    [Test]
    public void Filter_DirectoryEntry_MatchesByOwnRecordNumber()
    {
        // A directory itself is renamed/created: its own RecordNumber (not just its
        // ParentRecordNumber) must resolve to the repository root that contains it.
        var subdirectoryPath = Path.Combine(RepoRoot, "src");
        var scanRecords = new[]
        {
            Directory(RepoRootRecord, 1, RepoRoot),
            Directory(102, RepoRootRecord, subdirectoryPath),
        };
        var filter = new RepositoryChangeFilter(scanRecords, [RepoRoot]);

        var affected = filter.Filter([DirectoryEntry(102, RepoRootRecord, "src")]);

        Assert.That(affected, Is.EquivalentTo(new[] { RepoRoot }));
    }

    [Test]
    public void Filter_EntryOutsideAnyRepoRoot_IsIgnored()
    {
        var scanRecords = new[] { Directory(RepoRootRecord, 1, RepoRoot) };
        var filter = new RepositoryChangeFilter(scanRecords, [RepoRoot]);

        var affected = filter.Filter([FileEntry(300, 999, "unrelated.txt")]);

        Assert.That(affected, Is.Empty);
    }

    [Test]
    public void Filter_NestedRepository_PrefersInnerRoot()
    {
        var outerRoot = @"C:\repos\outer";
        var innerRoot = Path.Combine(outerRoot, "vendor", "inner");
        const ulong outerRootRecord = 10;
        const ulong innerRootRecord = 20;
        var scanRecords = new[]
        {
            Directory(outerRootRecord, 1, outerRoot),
            Directory(innerRootRecord, outerRootRecord, innerRoot),
        };
        var filter = new RepositoryChangeFilter(scanRecords, [outerRoot, innerRoot]);

        var affectedInner = filter.Filter([FileEntry(500, innerRootRecord, "README.md")]);
        var affectedOuter = filter.Filter([FileEntry(501, outerRootRecord, "README.md")]);

        Assert.That(affectedInner, Is.EquivalentTo(new[] { innerRoot }));
        Assert.That(affectedOuter, Is.EquivalentTo(new[] { outerRoot }));
    }

    [Test]
    public void Filter_MultipleReposOnOneDrive_EachMapsIndependently()
    {
        var repoA = @"C:\repos\alpha";
        var repoB = @"C:\repos\beta";
        const ulong repoARecord = 11;
        const ulong repoBRecord = 22;
        var scanRecords = new[]
        {
            Directory(repoARecord, 1, repoA),
            Directory(repoBRecord, 1, repoB),
        };
        var filter = new RepositoryChangeFilter(scanRecords, [repoA, repoB]);

        var affected = filter.Filter(
        [
            FileEntry(600, repoARecord, "a.txt"),
            FileEntry(601, repoBRecord, "b.txt"),
        ]);

        Assert.That(affected, Is.EquivalentTo(new[] { repoA, repoB }));
    }

    [Test]
    public void Filter_DuplicateAffectedEntries_ReturnsDistinctRoots()
    {
        var scanRecords = new[] { Directory(RepoRootRecord, 1, RepoRoot) };
        var filter = new RepositoryChangeFilter(scanRecords, [RepoRoot]);

        var affected = filter.Filter(
        [
            FileEntry(700, RepoRootRecord, "one.txt"),
            FileEntry(701, RepoRootRecord, "two.txt"),
            FileEntry(702, RepoRootRecord, "three.txt"),
        ]);

        Assert.That(affected.Count, Is.EqualTo(1));
        Assert.That(affected, Is.EquivalentTo(new[] { RepoRoot }));
    }

    [Test]
    public void Filter_RootPathCasingDiffersFromScanRecord_StillMatches()
    {
        var scanRecords = new[] { Directory(RepoRootRecord, 1, RepoRoot.ToUpperInvariant()) };
        var filter = new RepositoryChangeFilter(scanRecords, [RepoRoot.ToLowerInvariant()]);

        var affected = filter.Filter([FileEntry(800, RepoRootRecord, "index.lock")]);

        Assert.That(affected, Is.EquivalentTo(new[] { RepoRoot.ToLowerInvariant() }));
    }

    [Test]
    public void Filter_ScanRecordPathUsesForwardSlashBoundary_StillMatches()
    {
        var subdirectoryPath = RepoRoot + "/.git";
        var scanRecords = new[]
        {
            Directory(RepoRootRecord, 1, RepoRoot),
            Directory(101, RepoRootRecord, subdirectoryPath),
        };
        var filter = new RepositoryChangeFilter(scanRecords, [RepoRoot]);

        var affected = filter.Filter([FileEntry(202, 101, "HEAD")]);

        Assert.That(affected, Is.EquivalentTo(new[] { RepoRoot }));
    }

    [Test]
    public void Filter_FileScanRecordsAreNotIndexed_OnlyDirectoriesAre()
    {
        // A file ScanRecord sharing a repository root's own RecordNumber must not be
        // treated as if it were the root directory - only directory records are indexed.
        var scanRecords = new[] { File(RepoRootRecord, 1, RepoRoot) };
        var filter = new RepositoryChangeFilter(scanRecords, [RepoRoot]);

        var affected = filter.Filter([FileEntry(900, RepoRootRecord, "whatever")]);

        Assert.That(affected, Is.Empty);
    }

    static VolumeChangeEntry Modified(string fullPath) => new(fullPath, VolumeEntryKind.Modified);
    static VolumeChangeEntry Created(string fullPath) => new(fullPath, VolumeEntryKind.Created);
    static VolumeChangeEntry Deleted(string fullPath) => new(fullPath, VolumeEntryKind.Deleted);

    [Test]
    public void Classify_ModifiedEntryUnderTrackedRoot_MapsToChanged()
    {
        var filter = new RepositoryChangeFilter([], [RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Modified(Path.Combine(RepoRoot, "index.lock"))]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { RepoRoot }));
        Assert.That(result.Created, Is.Empty);
        Assert.That(result.Deleted, Is.Empty);
    }

    [Test]
    public void Classify_GitCreatedDirectlyUnderSearchRoot_MapsToCreatedParent()
    {
        var searchRoot = @"C:\repos";
        var newRepoGitPath = Path.Combine(searchRoot, "new-repo", ".git");
        var filter = new RepositoryChangeFilter([], [], [searchRoot]);

        var result = filter.Classify([Created(newRepoGitPath)]);

        Assert.That(result.Created, Is.EquivalentTo(new[] { Path.Combine(searchRoot, "new-repo") }));
        Assert.That(result.Changed, Is.Empty);
        Assert.That(result.Deleted, Is.Empty);
    }

    [Test]
    public void Classify_GitCreatedNestedUnderSearchRoot_MapsToCreatedParent()
    {
        var searchRoot = @"C:\repos";
        var newRepoGitPath = Path.Combine(searchRoot, "vendor", "deep", "new-repo", ".git");
        var filter = new RepositoryChangeFilter([], [], [searchRoot]);

        var result = filter.Classify([Created(newRepoGitPath)]);

        Assert.That(
            result.Created, Is.EquivalentTo(new[] { Path.Combine(searchRoot, "vendor", "deep", "new-repo") }));
    }

    [Test]
    public void Classify_GitCreatedOutsideAnySearchRoot_IsIgnored()
    {
        var filter = new RepositoryChangeFilter([], [], [@"C:\repos"]);

        var result = filter.Classify([Created(Path.Combine(@"C:\elsewhere\new-repo", ".git"))]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_GitCreatedWithNoSearchRootsConfigured_IsIgnored()
    {
        var filter = new RepositoryChangeFilter([], [RepoRoot]);

        var result = filter.Classify([Created(Path.Combine(@"C:\repos\new-repo", ".git"))]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_GitCreatedWithNoDirectorySeparator_IsIgnored()
    {
        var filter = new RepositoryChangeFilter([], [], [@"C:\repos"]);

        var result = filter.Classify([Created(".git")]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_NonGitDirectoryCreated_IsIgnored()
    {
        var searchRoot = @"C:\repos";
        var filter = new RepositoryChangeFilter([], [], [searchRoot]);

        var result = filter.Classify([Created(Path.Combine(searchRoot, "new-repo", "src"))]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_TrackedRootGitDirectoryDeleted_MapsToDeletedRoot()
    {
        var filter = new RepositoryChangeFilter([], [RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Deleted(Path.Combine(RepoRoot, ".git"))]);

        Assert.That(result.Deleted, Is.EquivalentTo(new[] { RepoRoot }));
        Assert.That(result.Changed, Is.Empty);
        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_TrackedRootItselfDeleted_MapsToDeletedRoot()
    {
        var filter = new RepositoryChangeFilter([], [RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Deleted(RepoRoot)]);

        Assert.That(result.Deleted, Is.EquivalentTo(new[] { RepoRoot }));
    }

    [Test]
    public void Classify_UnrelatedPathDeleted_IsIgnored()
    {
        var filter = new RepositoryChangeFilter([], [RepoRoot], [@"C:\repos"]);

        var result = filter.Classify([Deleted(@"C:\repos\unrelated\.git")]);

        Assert.That(result.Deleted, Is.Empty);
    }

    [Test]
    public void Classify_MixedBatch_PopulatesAllThreeCollectionsIndependently()
    {
        var searchRoot = @"C:\repos";
        var trackedRoot = Path.Combine(searchRoot, "existing-repo");
        var deletedRoot = Path.Combine(searchRoot, "gone-repo");
        var newRoot = Path.Combine(searchRoot, "new-repo");
        var filter = new RepositoryChangeFilter([], [trackedRoot, deletedRoot], [searchRoot]);

        var result = filter.Classify(
        [
            Modified(Path.Combine(trackedRoot, "index.lock")),
            Created(Path.Combine(newRoot, ".git")),
            Deleted(Path.Combine(deletedRoot, ".git")),
        ]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { trackedRoot }));
        Assert.That(result.Created, Is.EquivalentTo(new[] { newRoot }));
        Assert.That(result.Deleted, Is.EquivalentTo(new[] { deletedRoot }));
    }
}
