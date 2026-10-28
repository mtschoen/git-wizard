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
    public IReadOnlyDictionary<char, RootKey> Roots { get; } = Enumerable.Range('A', 26)
        .ToDictionary(value => (char)value, value => (RootKey)new VolumeKey((char)value));

    public FileIndexOptions ConfigureOptions(FileIndexOptions options) => ConfigureVolumeOptions(options,
        options.Root!.Value.TryGetVolume(out var requested) ? requested : new VolumeKey('X'));

    FileIndexOptions ConfigureVolumeOptions(FileIndexOptions options, VolumeKey volume)
    {
        var directory = volume.Letter == 'X' ? TreePath : Path.Combine(DirectoryPath, volume.ToString());
        Directory.CreateDirectory(directory);
        if (volume.Letter != 'X')
        {
            CopyTree(TreePath, directory);
        }
        return SyntheticVolumeFacts.Apply(options, 1, volume: volume, volumeRootDirectory: directory);
    }

    static void CopyTree(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), true);
        }
    }

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
    public Func<RootKey, CancellationToken, Task>? BeforeProduceAsync { get; set; }

    /// <summary>Answers each catch-up with a loss or null; a test sets it to fault a scan.</summary>
    public Func<RootKey, JournalCheckpointLoss?>? CatchUpLoss { get; set; }

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

    /// <summary>Opens one enumeration-backed index over the fixture tree for <paramref name="drive" />.</summary>
    public Task<FileIndex> OpenIndexAsync(char drive = 'X', RootKey? root = null) => FileIndex.OpenAsync(ConfigureOptions(new FileIndexOptions
    {
        Root = root ?? (RootKey)new VolumeKey(drive),
        CacheMode = CacheMode.Disabled,
        Producer = ProducerSelection.WalkOnly
    }), CancellationToken.None);

    /// <summary>Opens one enumeration-backed index per drive, in the order given.</summary>
    public Task<FileIndex[]> OpenIndicesAsync(params char[] drives) =>
        Task.WhenAll(drives.Select(drive => OpenIndexAsync(drive)));

    /// <summary>Opens one synthetic-producer index over the fixture tree for <paramref name="drive" />.</summary>
    public Task<FileIndex> OpenMftIndexAsync(ScriptedWatchSource source, char drive, RootKey? root = null) =>
        FileIndex.OpenAsync(ConfigureVolumeOptions(new FileIndexOptions
        {
            Root = root ?? (RootKey)new VolumeKey(drive),
            CacheMode = CacheMode.Disabled,
            MftSource = SyntheticIndexSource.Create(Producer, source)
        }, new VolumeKey(drive)), CancellationToken.None);

    /// <summary>Opens one synthetic-producer index per drive, in the order given, all sharing <paramref name="source" />.</summary>
    public Task<FileIndex[]> OpenMftIndicesAsync(ScriptedWatchSource source, params char[] drives) =>
        Task.WhenAll(drives.Select(drive => OpenMftIndexAsync(source, drive)));

    public async Task<GitWizard.MftIndexSession> OpenDirectorySessionAsync(IReadOnlyList<string> roots,
        ScriptedWatchSource? source = null)
    {
        var indices = await Task.WhenAll(roots.Select(root => source is null
            ? OpenIndexAsync(root: RootKey.FromDirectory(root))
            : OpenMftIndexAsync(source, 'X', RootKey.FromDirectory(root)))).ConfigureAwait(false);
        return new GitWizard.MftIndexSession(indices, null, roots, new Dictionary<string, string>());
    }

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
        new([index], null, drives, new Dictionary<string, string>());

    public static GitWizard.MftIndexSession Owner(IReadOnlyList<FileIndex> indices, params string[] drives) =>
        new(indices, null, drives, new Dictionary<string, string>());

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
