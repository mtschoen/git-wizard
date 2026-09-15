using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests;

[TestFixture]
public class IndexDiscoveryTests
{
    [Test]
    public async Task MarkerDirectoryParent()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        await using var index = await files.OpenIndexAsync();
        var markers = GitWizardApi.SelectGitEntries(index, includeGitFiles: false);
        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].Name, Is.EqualTo(".git"));
        Assert.That(markers[0].IsDirectory, Is.True);
        Assert.That(markers[0].Parent, Is.EqualTo(index.FindByName("repository").Single()));
        Assert.That(markers[0].Parent!.Value.IsDirectory, Is.True);
    }

    [Test]
    public async Task ScopedMarkerFileParent()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("worktree", isDirectory: false);
        await using var index = await files.OpenIndexAsync();
        var markers = GitWizardApi.SelectGitEntries(index, includeGitFiles: true);
        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].Name, Is.EqualTo(".git"));
        Assert.That(markers[0].IsDirectory, Is.False);
        Assert.That(markers[0].Parent, Is.EqualTo(index.FindByName("worktree").Single()));
    }

    [Test]
    public async Task DriveRootExcludesMarkerFiles()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        files.AddMarker("worktree", isDirectory: false);
        await using var index = await files.OpenIndexAsync();
        Assert.That(index.FindByName(".git"), Has.Count.EqualTo(2));
        var markers = GitWizardApi.SelectGitEntries(index, includeGitFiles: false);
        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].IsDirectory, Is.True);
        Assert.That(markers[0].Parent!.Value.Name, Is.EqualTo("repository"));
    }

    [Test]
    public async Task CaseInsensitiveMarkerLookup()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository", markerName: ".GIT");
        await using var index = await files.OpenIndexAsync();
        var markers = GitWizardApi.SelectGitEntries(index, includeGitFiles: false);
        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].Name, Is.EqualTo(".GIT"));
        Assert.That(markers[0].IsDirectory, Is.True);
        Assert.That(markers[0].Parent, Is.EqualTo(index.FindByName("repository").Single()));
    }
    [Test]
    public async Task DirectoryAndMarkerFileSelection()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        files.AddMarker("worktree", false);
        await using var index = await files.OpenIndexAsync();
        Assert.That(GitWizardApi.SelectGitEntries(index, false).Single().Parent!.Value.Name,
            Is.EqualTo("repository"));
        Assert.That(GitWizardApi.SelectGitEntries(index, true).Select(entry => entry.Parent!.Value.Name),
            Is.EquivalentTo(new[] { "repository", "worktree" }));
    }

    [Test]
    public async Task IgnoresFreeAndDeletedRows()
    {
        using var files = new PackedIndexFixture();
        await using var index = await OpenRowsAsync(files, (_, writer) =>
        {
            WriteRow(writer, 6, ".git", 5, RowFlags.Directory);
            WriteRow(writer, 7, ".git", 5, RowFlags.InUse | RowFlags.Directory);
            writer.MarkTombstone(7);
            WriteRow(writer, 8, "ordinary", 5, RowFlags.InUse);
        });
        Assert.That(index.FindByName("ordinary").Single().IsDirectory, Is.False);
        Assert.That(index.FindByName(".git"), Is.Empty);
        Assert.That(GitWizardApi.SelectGitEntries(index, true), Is.Empty);
    }

    [Test]
    public async Task RootRowNeedNotBeZero()
    {
        using var files = new PackedIndexFixture();
        await using var index = await OpenRowsAsync(files, (_, writer) =>
        {
            WriteRow(writer, 0, "$MFT", 5, RowFlags.InUse);
            WriteRow(writer, 7, "repository", 5, RowFlags.InUse | RowFlags.Directory);
            WriteRow(writer, 9, ".git", 7, RowFlags.InUse | RowFlags.Directory);
        });
        var marker = index.FindByName(".git").Single();
        Assert.That(marker.IsDirectory, Is.True);
        Assert.That(marker.Parent!.Value.Name, Is.EqualTo("repository"));
        var root = marker.Parent.Value.Parent!.Value;
        Assert.That(root.Name, Is.EqualTo("root"));
        Assert.That(root.Parent, Is.Null);
    }

    [Test]
    public async Task MalformedParentChainHonorsDocumentedDepthGuard()
    {
        using var files = new PackedIndexFixture();
        await using var index = await OpenRowsAsync(files, (_, writer) =>
        {
            for (uint row = 6; row < 6 + BlockLayout.MaximumPathDepth; row++)
                WriteRow(writer, row, "parent", row - 1, RowFlags.InUse | RowFlags.Directory);
            WriteRow(writer, 6 + BlockLayout.MaximumPathDepth, ".git",
                5 + BlockLayout.MaximumPathDepth, RowFlags.InUse | RowFlags.Directory);
        });
        var marker = index.FindByName(".git").Single();
        Assert.That(marker.Parent!.Value.Name, Is.EqualTo("parent"));
        Assert.That(() => marker.Path, Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task ResolveRepositoryRootCandidates_ExcessivePathDepth_SkipsMalformedEntry()
    {
        using var files = new PackedIndexFixture();
        await using var index = await OpenRowsAsync(files, (_, writer) =>
        {
            WriteRow(writer, 6, "validrepo", 5, RowFlags.InUse | RowFlags.Directory);
            WriteRow(writer, 7, ".git", 6, RowFlags.InUse | RowFlags.Directory);
            for (uint row = 8; row < 8 + BlockLayout.MaximumPathDepth; row++)
                WriteRow(writer, row, "parent", row - 1, RowFlags.InUse | RowFlags.Directory);
            WriteRow(writer, 8 + BlockLayout.MaximumPathDepth, ".git",
                7 + BlockLayout.MaximumPathDepth, RowFlags.InUse | RowFlags.Directory);
        });
        var entries = GitWizardApi.SelectGitEntries(index, includeGitFiles: true);
        Assert.That(entries, Has.Count.EqualTo(2));
        var candidates = GitWizardApi.ResolveRepositoryRootCandidates(index, entries, @"X:\", Array.Empty<string>()).ToArray();
        Assert.That(candidates, Is.EqualTo(new[] { Path.Combine(files.TreePath, "validrepo") }));
    }

    static Task<FileIndex> OpenRowsAsync(PackedIndexFixture files, Action<BlockFile, BlockWriter> writeRows) =>
        FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('X', files.TreePath, 1)],
            CacheDirectory = files.BlockDirectory,
            NoCache = true,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var block = BlockFile.Create(new BlockFileCreateOptions
                {
                    Path = request.BlockPath,
                    VolumeSerial = request.VolumeSerial,
                    ProducerKind = ProducerKind.Mft,
                    RootRow = 5,
                    SlotCapacity = 256,
                    NamePoolCapacity = 16384,
                    DeleteOnClose = request.DeleteOnClose
                });
                try
                {
                    var writer = new BlockWriter(block);
                    WriteRow(writer, 5, "root", 5, RowFlags.InUse | RowFlags.Directory);
                    writeRows(block, writer);
                    writer.Complete(PackedIndexFixture.CompletionTimestamp);
                    return Task.FromResult(new MftBlockProduceResult(block, 0, 0, 0, false));
                }
                catch
                {
                    block.Dispose();
                    throw;
                }
            }
        }, CancellationToken.None);

    static void WriteRow(BlockWriter writer, uint row, string name, uint parent, RowFlags flags) =>
        Assert.That(writer.TryWriteRow(row, name, new RowColumns(parent, flags, 0, 0, 0, 1)), Is.True);
}
