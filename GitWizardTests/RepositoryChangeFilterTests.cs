using GitWizard;
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
    public void Filter_FileScanRecordsAreNotIndexed_OnlyDirectoriesAre()
    {
        // A file ScanRecord sharing a repository root's own RecordNumber must not be
        // treated as if it were the root directory - only directory records are indexed.
        var scanRecords = new[] { File(RepoRootRecord, 1, RepoRoot) };
        var filter = new RepositoryChangeFilter(scanRecords, [RepoRoot]);

        var affected = filter.Filter([FileEntry(900, RepoRootRecord, "whatever")]);

        Assert.That(affected, Is.Empty);
    }
}
