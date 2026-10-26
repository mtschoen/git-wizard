using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

sealed class PackedIndexFixture : IDisposable
{
    internal static readonly DateTime CompletionTimestamp = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
    const uint RootRow = 5;
    readonly TempDirectory _directory = new("gw-packed-");
    public string DirectoryPath => _directory.Path;
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
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            File.WriteAllText(path, "gitdir: elsewhere");
        }

        return path;
    }

    public Task<FileIndex> OpenIndexAsync(params char[] drives) => FileIndex.OpenAsync(new FileIndexOptions
    {
        Drives = (IndexedDrive[])[.. (drives.Length == 0 ? ['X'] : drives)
            .Select(letter => new IndexedDrive(letter, TreePath, 1, ProducerKind.Enumeration))],
        CacheMode = CacheMode.Disabled
    }, CancellationToken.None);

    public Task<FileIndex> OpenMftIndexAsync(ScriptedWatchSource source, params char[] drives) =>
        FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = (IndexedDrive[])[.. drives.Select(letter => new IndexedDrive(letter, TreePath, 1))],
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
        {
            yield return row;
        }
    }

    static void WalkDirectory(string path, uint parent, ref uint nextRow, List<SyntheticRow> rows)
    {
        foreach (var directory in Directory.EnumerateDirectories(path).Order(StringComparer.Ordinal))
        {
            var row = nextRow++;
            rows.Add(DirectoryRow(row, Path.GetFileName(directory), parent, File.GetAttributes(directory) & FileAttributes.Hidden));
            WalkDirectory(directory, row, ref nextRow, rows);
        }
        var files = Directory.EnumerateFiles(path).Where(file =>
            string.Equals(Path.GetFileName(file), ".git", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(file), "HEAD", StringComparison.OrdinalIgnoreCase)).ToArray();
        var firstFileRow = nextRow;
        rows.AddRange(files.Select((file, index) => new SyntheticRow(firstFileRow + (uint)index, Path.GetFileName(file), parent)
        {
            Attributes = FileAttributes.Normal,
            Size = null,
            LastWriteTime = CompletionTimestamp,
            SequenceNumber = 1
        }));
        nextRow += (uint)files.Length;
    }

    static SyntheticRow DirectoryRow(uint row, string name, uint parent, FileAttributes extraAttributes = default)
    {
        return new SyntheticRow(row, name, parent)
        {
            IsDirectory = true,
            Attributes = FileAttributes.Directory | extraAttributes,
            LastWriteTime = CompletionTimestamp,
            SequenceNumber = 1
        };
    }

    public static GitWizard.MftIndexSession Owner(FileIndex index, params string[] drives) =>
        new(index, null, drives, [], new Dictionary<string, string>());

    public static SyntheticJournalRecord Entry(ulong record, ulong parent, string name, SyntheticJournalReason reason,
        bool directory = false) => new()
        {
            RecordNumber = record,
            ParentRecordNumber = parent,
            SequenceNumber = 1,
            FileName = name,
            Reason = reason,
            FileAttributes = directory ? FileAttributes.Directory : FileAttributes.Normal,
            Timestamp = CompletionTimestamp,
            Usn = 1001
        };

    public void Dispose() => _directory.Dispose();
}
