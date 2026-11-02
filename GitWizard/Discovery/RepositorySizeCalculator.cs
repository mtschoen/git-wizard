using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard.Discovery;

/// <summary>
/// Sums the bytes under each discovered repository from the index in one pass over each index's files, so a
/// refresh need not walk the directory tree. A size is the sum of the logical sizes the index holds for every
/// file at or below the repository directory, nested repositories and linked checkouts included, which is what
/// <see cref="GitWizardRepository.ComputeDirectorySize" /> reports. A repository the index cannot size
/// completely, or that no MFT scan covers (a non-NTFS volume, a walk), has no entry and the caller walks it instead.
/// </summary>
static class RepositorySizeCalculator
{
    const int CancellationCheckInterval = 4096;

    /// <summary>
    /// Most files without an index size a repository may hold before it is walked instead. Fewer are completed by
    /// reading just those files' lengths from the file system, so a few odd files do not cost a full walk.
    /// </summary>
    internal static int UnknownSizeFileLimit { get; set; } = 1000;

    /// <summary>Reads one file's length from the file system; null when it cannot be read. Tests replace it.</summary>
    internal static Func<string, long?> ReadFileLength { get; set; } = ReadLengthFromFileSystem;

    public static IReadOnlyDictionary<string, long> Compute(
        FileIndexSet index, IReadOnlyCollection<string> repositoryPaths, CancellationToken cancellationToken)
    {
        var sizes = new Dictionary<string, long>(WatchPath.StringComparer);
        var remaining = repositoryPaths.ToHashSet(WatchPath.StringComparer);
        var summary = new RepositorySizeSummary(repositoryPaths.Count);
        foreach (var member in index.Indices)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCompleteSnapshot(member))
                {
                    summary.NoteIncompleteRoot(member.Status, RootPathOf(member, cancellationToken));
                    continue;
                }

                summary.NoteCompleteRoot(RootPathOf(member, cancellationToken));

                var located = new List<(string Path, FileEntry Entry)>();
                foreach (var path in remaining)
                {
                    if (member.Find(path, cancellationToken) is { IsDirectory: true } entry)
                    {
                        located.Add((path, entry));
                    }
                }

                if (located.Count == 0)
                {
                    continue;
                }

                foreach (var (path, size) in SumMember(member, located, summary, cancellationToken))
                {
                    sizes[path] = size;
                }

                remaining.ExceptWith(located.Select(item => item.Path));
            }
            finally
            {
                // Discovery and sizing are done with this view. Keep ownership for Live, not its resident pages.
                member.ReleaseResidentPages();
            }
        }

        summary.NoteUnlocated(remaining);
        summary.NoteIndexed(sizes.Count);
        GitWizardLog.Log(summary.Describe());
        return sizes;
    }

    // The directory the index is rooted at, or its volume letter when a root that never loaded cannot name one.
    static string RootPathOf(FileIndex member, CancellationToken cancellationToken)
    {
        try
        {
            return member.Root(cancellationToken).Path;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or ObjectDisposedException)
        {
            return member.Status.Root.ToString();
        }
    }

    static long? ReadLengthFromFileSystem(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // A file the index lists but the file system no longer has contributes nothing to a walk either.
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    // Only a full MFT scan holds every file. A walk prunes .git directories and skipped subtrees, so a walk-produced
    // index undercounts and its repositories are walked instead; so is any root that is not ready or ran out of capacity.
    static bool IsCompleteSnapshot(FileIndex member) =>
        member.Status is
        {
            State: RootState.Ready,
            Producer: ProducerKind.Mft,
            Block: { IsCapacityExhausted: false, AccessDeniedSubtreeCount: 0 }
        };

    static List<(string Path, long Size)> SumMember(
        FileIndex member, List<(string Path, FileEntry Entry)> located, RepositorySizeSummary summary,
        CancellationToken cancellationToken)
    {
        var repositoryByRecord = new Dictionary<IndexRecordKey, int>();
        for (var position = 0; position < located.Count; position++)
        {
            repositoryByRecord[located[position].Entry.RecordKey] = position;
        }

        var owners = new OwnerResolver(repositoryByRecord);
        var totals = new long[located.Count];
        var unknown = new UnknownSizeFiles?[located.Count];
        var visited = 0;
        foreach (var file in member.Enumerate(new SearchQuery(null, IsDirectory: false), cancellationToken))
        {
            if (++visited % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var owner = owners.Resolve(file.Parent);
            if (owner < 0)
            {
                continue;
            }

            if (file.IsSizeKnown)
            {
                totals[owner] += file.Size;
            }
            else
            {
                (unknown[owner] ??= new UnknownSizeFiles()).Add(file.Path);
            }
        }

        // A nested repository's files also belong to every repository around it. Longer paths are nested deeper.
        foreach (var position in Enumerable.Range(0, located.Count).OrderByDescending(item => located[item].Path.Length))
        {
            var enclosing = owners.Resolve(located[position].Entry.Parent);
            if (enclosing >= 0)
            {
                totals[enclosing] += totals[position];
                if (unknown[position] is { } inherited)
                {
                    (unknown[enclosing] ??= new UnknownSizeFiles()).Merge(inherited);
                }
            }
        }

        return [.. located
            .Select((item, position) => (item.Path, Total: TryCompleteTotal(totals[position], unknown[position], summary)))
            .Where(item => item.Total.HasValue)
            .Select(item => (item.Path, item.Total.GetValueOrDefault()))];
    }

    // A repository whose index lacks a few file sizes is completed from the file system; one with too many, or one
    // whose odd files cannot be read, is left out so the caller walks it.
    static long? TryCompleteTotal(long indexedTotal, UnknownSizeFiles? unknown, RepositorySizeSummary summary)
    {
        if (unknown is null)
        {
            return indexedTotal;
        }

        if (unknown.Count > UnknownSizeFileLimit)
        {
            summary.NoteUnknownSizeTooMany(unknown.Count);
            return null;
        }

        var total = indexedTotal;
        foreach (var path in unknown.Paths)
        {
            if (ReadFileLength(path) is not { } length)
            {
                summary.NoteUnknownSizeUnreadable();
                return null;
            }

            total += length;
        }

        summary.NoteUnknownSizeResolved(unknown.Paths.Count);
        return total;
    }

    // The files under one repository whose index rows carry no size. Paths stop accumulating past the limit,
    // because such a repository is walked and the count alone says why.
    sealed class UnknownSizeFiles
    {
        public List<string> Paths { get; } = [];
        public long Count { get; private set; }

        public void Add(string path)
        {
            Count++;
            if (Paths.Count < UnknownSizeFileLimit)
            {
                Paths.Add(path);
            }
        }

        public void Merge(UnknownSizeFiles other)
        {
            Count += other.Count;
            Paths.AddRange(other.Paths.Take(UnknownSizeFileLimit - Paths.Count));
        }
    }

    // Maps a directory to the innermost repository at or above it (or -1), caching every directory climbed so
    // each directory's ancestry is read once however many files it holds.
    sealed class OwnerResolver(Dictionary<IndexRecordKey, int> repositoryByRecord)
    {
        readonly Dictionary<IndexRecordKey, int> _ownerByDirectory = [];

        public int Resolve(FileEntry? directory)
        {
            var climbed = new List<IndexRecordKey>();
            var owner = -1;
            for (var cursor = directory; cursor is { } current; cursor = current.Parent)
            {
                var key = current.RecordKey;
                if (_ownerByDirectory.TryGetValue(key, out var cached))
                {
                    owner = cached;
                    break;
                }

                climbed.Add(key);
                if (repositoryByRecord.TryGetValue(key, out var repository))
                {
                    owner = repository;
                    break;
                }
            }

            foreach (var key in climbed)
            {
                _ownerByDirectory[key] = owner;
            }

            return owner;
        }
    }
}
