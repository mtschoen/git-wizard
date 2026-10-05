using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

internal sealed class PackedIndexFixture : IDisposable
{
    internal static readonly DateTime CompletionTimestamp = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
    const uint RootRow = 5;
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "gw-packed-" + Guid.NewGuid().ToString("N"));
    public string TreePath { get; }
    public string BlockDirectory { get; }

    public PackedIndexFixture()
    {
        TreePath = Path.Combine(DirectoryPath, "tree");
        BlockDirectory = Path.Combine(DirectoryPath, "blocks");
        Directory.CreateDirectory(TreePath);
        Directory.CreateDirectory(BlockDirectory);
        Producer = new SyntheticMftProducer(_ => WalkTree())
        {
            JournalCursor = new MFTLib.UsnJournalCursor(JournalId, NextUsn),
            CompletedUtc = CompletionTimestamp
        };
    }

    /// <summary>
    /// Produces a block holding the tree under <see cref="TreePath"/> for any drive. A test sets its
    /// BeforeProduceAsync and CatchUpLoss to hold or fault a scan.
    /// </summary>
    public SyntheticMftProducer Producer { get; }

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

    public Task<FileIndex> OpenMftIndexAsync(ScriptedWatchSource source, params char[] drives) =>
        FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = drives.Select(letter => new IndexedDrive(letter, TreePath, 1)).ToArray(),
            CacheDirectory = BlockDirectory,
            NoCache = true,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = SyntheticIndexSource.Create(Producer, source)
        }, CancellationToken.None);

    public const ulong JournalId = 100;
    public const long NextUsn = 1000;

    IEnumerable<SyntheticRow> WalkTree()
    {
        yield return DirectoryRow(RootRow, "root", RootRow);
        var nextRow = RootRow + 1;
        var rows = new List<SyntheticRow>();
        WalkDirectory(TreePath, RootRow, ref nextRow, rows);
        foreach (var row in rows)
            yield return row;
    }

    static void WalkDirectory(string path, uint parent, ref uint nextRow, List<SyntheticRow> rows)
    {
        foreach (var directory in Directory.EnumerateDirectories(path).Order(StringComparer.Ordinal))
        {
            var row = nextRow++;
            rows.Add(DirectoryRow(row, Path.GetFileName(directory), parent));
            WalkDirectory(directory, row, ref nextRow, rows);
        }
        foreach (var file in Directory.EnumerateFiles(path).Where(file =>
            string.Equals(Path.GetFileName(file), ".git", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(file), "HEAD", StringComparison.OrdinalIgnoreCase)))
        {
            rows.Add(new SyntheticRow(nextRow++, Path.GetFileName(file), parent)
            {
                Attributes = FileAttributes.Normal,
                Size = null,
                ModifiedUtc = CompletionTimestamp,
                SequenceNumber = 1
            });
        }
    }

    static SyntheticRow DirectoryRow(uint row, string name, uint parent) => new(row, name, parent)
    {
        IsDirectory = true,
        Attributes = FileAttributes.Directory,
        ModifiedUtc = CompletionTimestamp,
        SequenceNumber = 1
    };

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
