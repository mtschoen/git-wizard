using GitWizard;
using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizardTests.Watch;

[TestFixture]
public class IndexChangeMappingTests
{
    string _fixtureDirectory = null!;
    FileIndex? _index;

    [SetUp]
    public async Task SetUp()
    {
        _fixtureDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"index-change-mapping-{Guid.NewGuid():N}");
        var treeDirectory = Path.Combine(_fixtureDirectory, "tree");
        Directory.CreateDirectory(Path.Combine(treeDirectory, "current", ".git"));
        await File.WriteAllTextAsync(Path.Combine(treeDirectory, "current", "file.txt"), "content");
        _index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', treeDirectory, 1)],
            CacheDirectory = Path.Combine(_fixtureDirectory, "cache"),
            NoCache = true,
            ProducerPolicy = ProducerPolicy.Enumeration
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
        var change = new FileChange(FileChangeKind.Created, Entry("file.txt", false),
            @"T:\captured\file.txt");

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
        var change = new FileChange(FileChangeKind.Deleted, Entry("file.txt", false),
            @"T:\deleted\file.txt");

        Assert.That(change.Entry.Path, Is.Not.EqualTo(change.Path));
        Assert.That(IndexChangeMapping.Map(change), Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"T:\deleted\file.txt", VolumeEntryKind.Deleted)
        }));
    }

    [Test]
    public void ModifiedUsesCapturedPath()
    {
        var change = new FileChange(FileChangeKind.Modified, Entry("file.txt", false),
            @"T:\modified\file.txt");

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
        var change = new FileChange(FileChangeKind.Renamed, Entry("file.txt", false),
            @"T:\new\file.txt", @"T:\old\file.txt");
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

        var markerChange = new FileChange(FileChangeKind.Renamed, Entry(".git", true),
            @"T:\new\.git", @"T:\old\.git");
        ObserveDivergence(markerChange);
        var markerEntries = IndexChangeMapping.Map(markerChange);
        Assert.That(markerEntries, Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"T:\old\.git", VolumeEntryKind.Deleted),
            new VolumeChangeEntry(@"T:\new\.git", VolumeEntryKind.Created)
        }));
        var markerFilter = new RepositoryChangeFilter([@"T:\old"], [@"T:\"]);
        var markerResult = markerFilter.Classify(markerEntries);
        Assert.That(markerResult.Changed, Is.Empty);
        Assert.That(markerResult.Deleted, Is.EqualTo(new[] { @"T:\old" }));
        Assert.That(markerResult.Created, Is.EqualTo(new[] { @"T:\new" }));

        var withoutPreviousPath = IndexChangeMapping.Map(markerChange with { PreviousPath = null });
        Assert.That(withoutPreviousPath, Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"T:\new\.git", VolumeEntryKind.Created)
        }));
        var withoutPreviousResult = markerFilter.Classify(withoutPreviousPath);
        Assert.That(withoutPreviousResult.Deleted, Is.Empty);
        Assert.That(withoutPreviousResult.Created, Is.EqualTo(new[] { @"T:\new" }));
    }

    [Test]
    public void SameBatchRenameDoesNotRewriteEarlierEvent()
    {
        var entry = Entry("file.txt", false);
        var earlierChange = new FileChange(FileChangeKind.Modified, entry, @"T:\old\file.txt");
        var renameChange = new FileChange(FileChangeKind.Renamed, entry,
            @"T:\current\file.txt", earlierChange.Path);
        ObserveDivergence(earlierChange);
        FileChange[] batch = [earlierChange, renameChange];

        var earlierEntries = IndexChangeMapping.Map(batch[0]);
        Assert.That(earlierEntries, Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"T:\old\file.txt", VolumeEntryKind.Modified)
        }));
        var filter = new RepositoryChangeFilter([@"T:\old", @"T:\current"]);
        var earlierResult = filter.Classify(earlierEntries);
        Assert.That(earlierResult.Changed, Is.EqualTo(new[] { @"T:\old" }));
        var result = filter.Classify(batch.SelectMany(IndexChangeMapping.Map).ToArray());
        Assert.That(result.Changed, Is.EquivalentTo(new[] { @"T:\old", @"T:\current" }));
        Assert.That(result.Created, Is.Empty);
        Assert.That(result.Deleted, Is.Empty);
        Assert.That(earlierChange.Path, Is.EqualTo(@"T:\old\file.txt"));
    }

    FileEntry Entry(string name, bool isDirectory)
    {
        var entries = _index!.FindByName(name);
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
