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

    public static IReadOnlyDictionary<string, long> Compute(
        FileIndexSet index, IReadOnlyCollection<string> repositoryPaths, CancellationToken cancellationToken)
    {
        var sizes = new Dictionary<string, long>(WatchPath.StringComparer);
        var remaining = repositoryPaths.ToHashSet(WatchPath.StringComparer);
        foreach (var member in index.Indices.Where(IsCompleteSnapshot))
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            foreach (var (path, size) in SumMember(member, located, cancellationToken))
            {
                sizes[path] = size;
            }

            remaining.ExceptWith(located.Select(item => item.Path));
        }

        return sizes;
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
        FileIndex member, List<(string Path, FileEntry Entry)> located, CancellationToken cancellationToken)
    {
        var repositoryByRecord = new Dictionary<IndexRecordKey, int>();
        for (var position = 0; position < located.Count; position++)
        {
            repositoryByRecord[located[position].Entry.RecordKey] = position;
        }

        var owners = new OwnerResolver(repositoryByRecord);
        var totals = new long[located.Count];
        var incomplete = new bool[located.Count];
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
                incomplete[owner] = true;
            }
        }

        // A nested repository's files also belong to every repository around it. Longer paths are nested deeper.
        foreach (var position in Enumerable.Range(0, located.Count).OrderByDescending(item => located[item].Path.Length))
        {
            var enclosing = owners.Resolve(located[position].Entry.Parent);
            if (enclosing >= 0)
            {
                totals[enclosing] += totals[position];
                incomplete[enclosing] |= incomplete[position];
            }
        }

        return [.. Enumerable.Range(0, located.Count)
            .Where(position => !incomplete[position])
            .Select(position => (located[position].Path, totals[position]))];
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
