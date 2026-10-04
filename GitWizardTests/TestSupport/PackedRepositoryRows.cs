using MFTLib.Index;

namespace GitWizardTests.TestSupport;

internal static class PackedRepositoryRows
{
    public static Task<FileIndex> OpenRowsAsync(PackedIndexFixture files, Action<BlockFile, BlockWriter> writeRows) =>
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
                    writer.Complete(PackedIndexFixture.CompletionTimestamp, null);
                    return Task.FromResult(new MftBlockProduceResult(block, 0, 0, 0));
                }
                catch
                {
                    block.Dispose();
                    throw;
                }
            }
        }, CancellationToken.None);

    public static void WriteRow(BlockWriter writer, uint row, string name, uint parent, RowFlags flags) =>
        Assert.That(writer.TryWriteRow(row, name, new RowColumns(parent, flags, 0, 0, 0, 1)), Is.True);
}
