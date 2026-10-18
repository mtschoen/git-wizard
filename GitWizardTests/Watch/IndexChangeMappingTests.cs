using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Watch;

[TestFixture]
public class IndexChangeMappingTests
{
    static readonly DateTime ChangeTime = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    string _fixtureDirectory = null!;
    FileIndex? _index;

    [SetUp]
    public async Task SetUp()
    {
        _fixtureDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"index-change-mapping-{Guid.NewGuid():N}");
        var treeDirectory = Path.Combine(_fixtureDirectory, "tree");
        Directory.CreateDirectory(Path.Combine(treeDirectory, "current", ".git"));
        Directory.CreateDirectory(Path.Combine(treeDirectory, "current", "sub"));
        await File.WriteAllTextAsync(Path.Combine(treeDirectory, "current", "file.txt"), "content");
        _index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', treeDirectory, 1, ProducerKind.Enumeration)],
            CacheMode = CacheMode.Disabled
        }, CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_index is not null)
            await _index.DisposeAsync();
        if (Directory.Exists(_fixtureDirectory))
            Directory.Delete(_fixtureDirectory, recursive: true);
    }

    [Test]
    public void CreatedMapsToCreated()
    {
        var change = SyntheticNotifications.CreateFileChange(FileChangeKind.Created, Entry("file.txt", false),
            @"T:\captured\file.txt", ChangeTime);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IndexChangeMapping.Map(change with { Kind = (FileChangeKind)(-1) }));
        Assert.That(IndexChangeMapping.Map(change), Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"T:\captured\file.txt", VolumeEntryKind.Created)
        }));
    }

    [Test]
    public void DeletedUsesCapturedPath()
    {
        var change = SyntheticNotifications.CreateFileChange(FileChangeKind.Deleted, Entry("file.txt", false),
            @"T:\deleted\file.txt", ChangeTime);

        Assert.That(change.Entry.Path, Is.Not.EqualTo(change.Path));
        Assert.That(IndexChangeMapping.Map(change), Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"T:\deleted\file.txt", VolumeEntryKind.Deleted)
        }));
    }

    [Test]
    public void ModifiedUsesCapturedPath()
    {
        var change = SyntheticNotifications.CreateFileChange(FileChangeKind.Modified, Entry("file.txt", false),
            @"T:\modified\file.txt", ChangeTime);

        Assert.That(change.Entry.Path, Is.Not.EqualTo(change.Path));
        Assert.That(IndexChangeMapping.Map(change), Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"T:\modified\file.txt", VolumeEntryKind.Modified)
        }));
    }

    [Test]
    public void RenameInvalidatesBothLocations()
    {
        // P17 constructs captured locations because enumeration blocks reject journal mutation.
        var change = SyntheticNotifications.CreateFileChange(FileChangeKind.Renamed, Entry("file.txt", false),
            @"T:\new\file.txt", ChangeTime, previousPath: @"T:\old\file.txt");
        ObserveDivergence(change);
        var entries = IndexChangeMapping.Map(change);
        Assert.That(entries, Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"T:\old\file.txt", VolumeEntryKind.Deleted),
            new VolumeChangeEntry(@"T:\new\file.txt", VolumeEntryKind.Created)
        }));
        var result = new RepositoryChangeFilter([@"T:\old", @"T:\new"]).Classify(entries);
        Assert.That(result.Changed, Is.EquivalentTo(new[] { @"T:\old", @"T:\new" }));
        Assert.That(result.Created, Is.Empty);
        Assert.That(result.Deleted, Is.Empty);

        var markerChange = SyntheticNotifications.CreateFileChange(FileChangeKind.Renamed, Entry(".git", true),
            @"T:\new\.git", ChangeTime, previousPath: @"T:\old\.git");
        ObserveDivergence(markerChange);
        var markerEntries = IndexChangeMapping.Map(markerChange);
        Assert.That(markerEntries, Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"T:\old\.git", VolumeEntryKind.Deleted),
            new VolumeChangeEntry(@"T:\new\.git", VolumeEntryKind.MovedIn)
        }));
        var markerFilter = new RepositoryChangeFilter([@"T:\old"], [@"T:\"]);
        var markerResult = markerFilter.Classify(markerEntries);
        Assert.That(markerResult.Changed, Is.Empty);
        Assert.That(markerResult.Deleted, Is.EqualTo(new[] { @"T:\old" }));
        Assert.That(markerResult.Created, Is.Empty,
            "A directory rename no longer yields Created; MovedIn candidate classification is separate.");

        var withoutPreviousPath = IndexChangeMapping.Map(markerChange with { PreviousPath = null });
        Assert.That(withoutPreviousPath, Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"T:\new\.git", VolumeEntryKind.MovedIn)
        }));
    }

    [Test]
    public void DirectoryRenameMapsToDeletedPlusMovedIn()
    {
        var change = SyntheticNotifications.CreateFileChange(FileChangeKind.Renamed, Entry("sub", true),
            @"T:\current\sub-renamed", ChangeTime, previousPath: @"T:\current\sub");
        ObserveDivergence(change);

        var entries = IndexChangeMapping.Map(change);

        Assert.That(entries, Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"T:\current\sub", VolumeEntryKind.Deleted),
            new VolumeChangeEntry(@"T:\current\sub-renamed", VolumeEntryKind.MovedIn)
        }));
    }

    [Test]
    public void DirectoryRenameWithoutPreviousPathMapsToMovedInAlone()
    {
        var change = SyntheticNotifications.CreateFileChange(FileChangeKind.Renamed, Entry("sub", true),
            @"T:\current\sub-renamed", ChangeTime);
        ObserveDivergence(change);

        var entries = IndexChangeMapping.Map(change);

        Assert.That(entries, Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"T:\current\sub-renamed", VolumeEntryKind.MovedIn)
        }));
    }

    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    [TestCase(false, false, true, TestName = "Synthetic_ModifiedRename")]
    [TestCase(true, false, true, TestName = "PR319_RealMft_FileRenameDeleteRecreate")]
    [TestCase(true, true, true, TestName = "PR319_RealMft_DirectoryRenameDeleteRecreate")]
    [TestCase(true, false, false, TestName = "PR319_RealMft_FileRenameDeleteRecreateWithoutPreviousPath")]
    [TestCase(true, true, false, TestName = "PR319_RealMft_DirectoryRenameDeleteRecreateWithoutPreviousPath")]
    public async Task SameBatchRenameDoesNotRewriteEarlierEvent(bool useRealMftBatch, bool isDirectory, bool hasPreviousPath)
    {
        if (!useRealMftBatch)
        {
            var entry = Entry("file.txt", false);
            var earlierChange = SyntheticNotifications.CreateFileChange(FileChangeKind.Modified, entry, @"T:\old\file.txt", ChangeTime);
            var syntheticRenameChange = SyntheticNotifications.CreateFileChange(FileChangeKind.Renamed, entry,
                @"T:\current\file.txt", ChangeTime, previousPath: earlierChange.Path);
            ObserveDivergence(earlierChange);
            FileChange[] batch = [earlierChange, syntheticRenameChange];

            var earlierEntries = IndexChangeMapping.Map(batch[0]);
            Assert.That(earlierEntries, Is.EqualTo(new[]
            {
                new VolumeChangeEntry(@"T:\old\file.txt", VolumeEntryKind.Modified)
            }));
            var syntheticFilter = new RepositoryChangeFilter([@"T:\old", @"T:\current"]);
            var earlierResult = syntheticFilter.Classify(earlierEntries);
            Assert.That(earlierResult.Changed, Is.EqualTo(new[] { @"T:\old" }));
            var syntheticResult = syntheticFilter.Classify(batch.SelectMany(IndexChangeMapping.Map).ToArray());
            Assert.That(syntheticResult.Changed, Is.EquivalentTo(new[] { @"T:\old", @"T:\current" }));
            Assert.That(syntheticResult.Created, Is.Empty);
            Assert.That(syntheticResult.Deleted, Is.Empty);
            Assert.That(earlierChange.Path, Is.EqualTo(@"T:\old\file.txt"));
            return;
        }

        using var files = new PackedIndexFixture();
        files.AddMarker("current", isDirectory: isDirectory);
        var scripted = new ScriptedWatchSource();
        await using var index = await files.OpenMftIndexAsync(scripted, 'X');
        var changes = new List<FileChange>();
        index.Changed += change =>
        {
            lock (changes)
                changes.Add(change);
        };
        await index.StartWatchingAsync(['X'], CancellationToken.None);
        await scripted.WaitForStartAsync('X', 1, CancellationToken.None).WaitAsync(HangGuard);

        var targetName = isDirectory ? "current" : ".git";
        var targetEntry = index.Search(new SearchQuery(targetName, NameMatchMode.Exact)).Single();
        var targetRecord = targetEntry.RecordKey.RecordNumber;
        var parentRecord = targetEntry.Parent!.Value.RecordKey.RecordNumber;
        var renamedName = targetName + "-renamed";
        var replacementName = targetName + "-replacement";

        await scripted.WatchFor('X').PublishBatchAsync([
            Record(targetRecord, parentRecord, renamedName, SyntheticJournalReason.RenameNewName, 1, 1001, isDirectory),
            Record(targetRecord, parentRecord, renamedName, SyntheticJournalReason.FileDelete, 1, 1002, isDirectory),
            Record(targetRecord, parentRecord, replacementName, SyntheticJournalReason.FileCreate, 2, 1003, isDirectory)
        ], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1003));

        Assert.That(changes, Has.Count.EqualTo(3));
        var renameChange = changes[0];
        Assert.That(renameChange.Kind, Is.EqualTo(FileChangeKind.Renamed));
        Assert.That(renameChange.Entry.IsDeleted, Is.True, "The earlier rename entry is superseded.");
        Assert.Throws<InvalidOperationException>(() => _ = renameChange.Entry.IsDirectory);

        if (!hasPreviousPath)
            renameChange = renameChange with { PreviousPath = null };

        IReadOnlyList<VolumeChangeEntry> mappedEntries = null!;
        Assert.DoesNotThrow(() => mappedEntries = IndexChangeMapping.Map(renameChange));
        var expected = new List<VolumeChangeEntry>();
        if (hasPreviousPath)
            expected.Add(new VolumeChangeEntry(renameChange.PreviousPath!, VolumeEntryKind.Deleted));
        expected.Add(new VolumeChangeEntry(renameChange.Path, VolumeEntryKind.MovedIn));
        Assert.That(mappedEntries, Is.EqualTo(expected));

        var root = index.Root('X').Path;
        var discoveryFilter = new RepositoryChangeFilter([], [root], includeTempDirectories: true);
        Assert.That(discoveryFilter.Classify(mappedEntries).MovedInCandidates, Is.EqualTo(new[] { renameChange.Path }),
            "An unknown type must reach probing rather than silently discard an untracked directory.");
        var markerRename = renameChange with { Path = Path.Combine(root, "newrepo", ".git") };
        Assert.That(discoveryFilter.Classify(IndexChangeMapping.Map(markerRename)).MovedInCandidates,
            Is.EqualTo(new[] { Path.Combine(root, "newrepo") }), "Gitfile markers must still reach repository probing.");

        var allMapped = changes.SelectMany(IndexChangeMapping.Map).ToArray();
        Assert.That(allMapped, Has.Length.EqualTo(4));
        var filter = new RepositoryChangeFilter([Path.Combine(root, "current"), Path.Combine(root, "current-renamed")], includeTempDirectories: true);
        var filterResult = filter.Classify(allMapped);
        Assert.That(filterResult.Deleted, Is.Not.Empty);
    }

    static SyntheticJournalRecord Record(
        ulong recordNumber, ulong parentNumber, string name, SyntheticJournalReason reason, ushort sequence, long usn,
        bool isDirectory) =>
        new()
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentNumber,
            SequenceNumber = sequence,
            FileName = name,
            Reason = reason | SyntheticJournalReason.Close,
            FileAttributes = isDirectory ? FileAttributes.Directory : FileAttributes.Normal,
            Timestamp = PackedIndexFixture.CompletionTimestamp,
            Usn = usn
        };

    FileEntry Entry(string name, bool isDirectory)
    {
        var entries = _index!.Search(new SearchQuery(name, NameMatchMode.Exact));
        Assert.That(entries, Has.Count.EqualTo(1));
        var entry = entries[0];
        Assert.That(entry.Name, Is.EqualTo(name));
        Assert.That(entry.IsDirectory, Is.EqualTo(isDirectory));
        Assert.That(entry.Parent!.Value.Name, Is.EqualTo("current"));
        return entry;
    }

    static void ObserveDivergence(FileChange change)
    {
        TestContext.Out.WriteLine($"Entry.Path = '{change.Entry.Path}'; Path = '{change.Path}'");
        Assert.That(change.Entry.Path, Is.Not.EqualTo(change.Path));
    }
}
