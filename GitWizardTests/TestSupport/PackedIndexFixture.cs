using MFTLib;
using MFTLib.Index;

namespace GitWizardTests.TestSupport;

internal sealed class PackedIndexFixture : IDisposable
{
    internal static readonly DateTime CompletionTimestamp = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
    readonly List<BlockFile> _blocks = [];
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "gw-packed-" + Guid.NewGuid().ToString("N"));
    public string TreePath { get; }
    public string BlockDirectory { get; }

    public PackedIndexFixture()
    {
        TreePath = Path.Combine(DirectoryPath, "tree");
        BlockDirectory = Path.Combine(DirectoryPath, "blocks");
        Directory.CreateDirectory(TreePath);
        Directory.CreateDirectory(BlockDirectory);
    }

    public string AddMarker(string repository, bool isDirectory = true, string markerName = ".git")
    {
        var path = Path.Combine(TreePath, repository, markerName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (isDirectory)
            Directory.CreateDirectory(path);
        else
            File.WriteAllText(path, "gitdir: elsewhere");
        return path;
    }

    public Task<FileIndex> OpenIndexAsync(params char[] drives) => FileIndex.OpenAsync(new FileIndexOptions
    {
        Drives = (drives.Length == 0 ? ['X'] : drives).Select(letter => new IndexedDrive(letter, TreePath, 1)).ToArray(),
        CacheDirectory = BlockDirectory,
        NoCache = true,
        ProducerPolicy = ProducerPolicy.Enumeration
    }, CancellationToken.None);

    public Task<FileIndex> OpenMftIndexAsync(IIndexWatchSource source, params char[] drives) =>
        FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = drives.Select(letter => new IndexedDrive(letter, TreePath, 1)).ToArray(),
            CacheDirectory = BlockDirectory,
            NoCache = true,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = ProduceMftAsync,
            WatchSource = source
        }, CancellationToken.None);

    public const ulong JournalId = 100;
    public const long NextUsn = 1000;

    /// <summary>Awaited before each MFT production, so a test can hold a rescan in progress.</summary>
    public Func<CancellationToken, Task>? BeforeProduceAsync { get; set; }

    /// <summary>The catch-up loss each MFT production reports; null reports none.</summary>
    public Func<JournalCheckpointLoss?>? CatchUpLossForProduction { get; set; }

    internal async Task<MftBlockProduceResult> ProduceMftAsync(MftBlockProduceRequest request,
        CancellationToken cancellationToken)
    {
        if (BeforeProduceAsync is { } beforeProduce)
            await beforeProduce(cancellationToken).ConfigureAwait(false);
        return ProduceMft(request, cancellationToken);
    }

    MftBlockProduceResult ProduceMft(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = request.BlockPath,
            VolumeSerial = request.VolumeSerial,
            ProducerKind = ProducerKind.Mft,
            SlotCapacity = 256,
            NamePoolCapacity = 16384,
            DeleteOnClose = request.DeleteOnClose
        });
        try
        {
            var writer = new BlockWriter(block);
            WriteDirectory(writer, 5, "root", 5);
            uint row = 6;
            WriteTree(writer, TreePath, 5, ref row);
            writer.SetJournalCursor(JournalId, NextUsn);
            writer.Complete(CompletionTimestamp, null);
            return new MftBlockProduceResult(block, JournalId, NextUsn, 0)
            {
                CatchUpLoss = CatchUpLossForProduction?.Invoke()
            };
        }
        catch
        {
            block.Dispose();
            throw;
        }
    }

    static void WriteTree(BlockWriter writer, string path, uint parent, ref uint nextRow)
    {
        foreach (var directory in Directory.EnumerateDirectories(path).Order(StringComparer.Ordinal))
        {
            var row = nextRow++;
            WriteDirectory(writer, row, Path.GetFileName(directory), parent);
            WriteTree(writer, directory, row, ref nextRow);
        }
        foreach (var file in Directory.EnumerateFiles(path).Where(file =>
            string.Equals(Path.GetFileName(file), ".git", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(file), "HEAD", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.That(writer.TryWriteRow(nextRow++, Path.GetFileName(file), new RowColumns(parent,
                RowFlags.InUse | RowFlags.SizeUnknown, (uint)FileAttributes.Normal, 0,
                CompletionTimestamp.Ticks, 1)), Is.True);
        }
    }

    static void WriteDirectory(BlockWriter writer, uint row, string name, uint parent) =>
        Assert.That(writer.TryWriteRow(row, name, new RowColumns(parent,
            RowFlags.InUse | RowFlags.Directory, (uint)FileAttributes.Directory, 0,
            CompletionTimestamp.Ticks, 1)), Is.True);

    public BlockFile Produce()
    {
        var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(BlockDirectory, Guid.NewGuid().ToString("N") + ".mlix"),
            VolumeSerial = 1,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = 256,
            NamePoolCapacity = 16384,
            DeleteOnClose = true
        });
        _blocks.Add(block);
        var writer = new BlockWriter(block);
        var result = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = TreePath,
            DriveLetter = 'X'
        }).Produce(writer, null, CancellationToken.None);
        Assert.That(result.CompactionNeeded, Is.False);
        Assert.That(result.AccessDeniedSubtreeCount, Is.Zero);
        writer.Complete(CompletionTimestamp, null);
        return block;
    }

    public static Dictionary<string, BlockScanOutcome> Outcomes(BlockFile block, string drive = "X") => new()
    {
        [drive] = new(block, 0)
    };

    public static void AssertMarker(BlockFile block, string parentName, bool isDirectory)
    {
        var found = false;
        for (uint rowIndex = 0; rowIndex < block.Header.RowCount; rowIndex++)
        {
            var row = block.Rows[(int)rowIndex];
            if (!NamePool.ReadRowName(block, rowIndex).SequenceEqual(".git") || row.IsDirectory != isDirectory)
                continue;
            Assert.That(row.ParentRow, Is.LessThan(block.Header.RowCount));
            Assert.That(block.Rows[(int)row.ParentRow].IsDirectory, Is.True);
            if (NamePool.ReadRowName(block, row.ParentRow).SequenceEqual(parentName))
                found = true;
        }
        Assert.That(found, Is.True, $"Missing .git marker under {parentName} (directory: {isDirectory}).");
    }

    public void Dispose()
    {
        foreach (var block in _blocks)
            block.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
