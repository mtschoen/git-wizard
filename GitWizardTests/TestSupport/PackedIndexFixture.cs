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
            JournalCursor = new SyntheticJournalCursor(JournalIdentifier, NextUsn),
            ScanTimestamp = CompletionTimestamp,
            BeforeProduceAsync = (drive, token) => BeforeProduceAsync?.Invoke(drive, token) ?? Task.CompletedTask,
            CatchUpLoss = drive => CatchUpLoss?.Invoke(drive)
        };
    }

    /// <summary>Produces a block holding the tree under <see cref="TreePath"/> for any drive.</summary>
    public SyntheticMftProducer Producer { get; }

    /// <summary>Awaited before every production; a test sets it to hold a scan. The producer's own property is init-only, so it forwards here.</summary>
    public Func<DriveKey, CancellationToken, Task>? BeforeProduceAsync { get; set; }

    /// <summary>Answers each catch-up with a loss or null; a test sets it to fault a scan.</summary>
    public Func<DriveKey, JournalCheckpointLoss?>? CatchUpLoss { get; set; }

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
        Drives = (drives.Length == 0 ? ['X'] : drives)
            .Select(letter => new IndexedDrive(letter, TreePath, 1, ProducerPolicy.Enumeration)).ToArray(),
        CacheMode = CacheMode.Disabled
    }, CancellationToken.None);

    public Task<FileIndex> OpenMftIndexAsync(ScriptedWatchSource source, params char[] drives) =>
        FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = drives.Select(letter => new IndexedDrive(letter, TreePath, 1)).ToArray(),
            CacheMode = CacheMode.Disabled,
            MftSource = SyntheticIndexSource.Create(Producer, source)
        }, CancellationToken.None);

    public const ulong JournalIdentifier = 100;
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
                LastWriteTime = CompletionTimestamp,
                SequenceNumber = 1
            });
        }
    }

    static SyntheticRow DirectoryRow(uint row, string name, uint parent) => new(row, name, parent)
    {
        IsDirectory = true,
        Attributes = FileAttributes.Directory,
        LastWriteTime = CompletionTimestamp,
        SequenceNumber = 1
    };

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
