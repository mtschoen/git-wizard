using MFTLib;

namespace GitWizard;

/// <summary>
/// Maps USN journal batches back to the tracked repository roots they touched. Built once
/// per drive from that drive's cold-scan <see cref="ScanRecord"/>s and the repository roots
/// being watched on it, then reused for every subsequent <see cref="Filter"/> call against
/// live journal batches from the same drive.
/// </summary>
/// <remarks>
/// Pure and cross-platform-testable: it only consumes MFTLib's plain data types
/// (<see cref="ScanRecord"/>, <see cref="UsnJournalEntry"/>) and never touches the
/// filesystem, so it can be exercised with synthetic records on any OS even though the
/// journal-watch feature itself is Windows-only.
/// </remarks>
public sealed class RepositoryChangeFilter
{
    readonly Dictionary<ulong, string> _repositoryRootByRecordNumber;

    /// <summary>
    /// Build the filter's record-number index for one drive.
    /// </summary>
    /// <param name="scanRecords">Cold-scan records for one drive.</param>
    /// <param name="repositoryRootPaths">Tracked repository roots on that same drive.</param>
    public RepositoryChangeFilter(
        IReadOnlyList<ScanRecord> scanRecords, IReadOnlyCollection<string> repositoryRootPaths)
    {
        _repositoryRootByRecordNumber = BuildIndex(scanRecords, repositoryRootPaths);
    }

    /// <summary>
    /// Returns the distinct repository roots affected by <paramref name="batch"/>. An entry
    /// is affected if its parent directory (or, for a directory entry, the directory itself)
    /// is under a tracked repository root.
    /// </summary>
    public IReadOnlyCollection<string> Filter(UsnJournalEntry[] batch)
    {
        var affectedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in batch)
        {
            if (_repositoryRootByRecordNumber.TryGetValue(entry.ParentRecordNumber, out var parentRoot))
                affectedRoots.Add(parentRoot);

            var isDirectoryEntry = (entry.FileAttributes & FileAttributes.Directory) != 0;
            if (isDirectoryEntry
                && _repositoryRootByRecordNumber.TryGetValue(entry.RecordNumber, out var directoryRoot))
                affectedRoots.Add(directoryRoot);
        }

        return affectedRoots;
    }

    // Longest-root-first so a nested repository root wins the mapping for directories under
    // it, rather than the outer repository that also contains it.
    static Dictionary<ulong, string> BuildIndex(
        IReadOnlyList<ScanRecord> scanRecords, IReadOnlyCollection<string> repositoryRootPaths)
    {
        var orderedRoots = repositoryRootPaths
            .Select(root => root.TrimEnd('\\', '/'))
            .OrderByDescending(root => root.Length)
            .ToArray();

        var index = new Dictionary<ulong, string>();
        foreach (var record in scanRecords)
        {
            if (!record.IsDirectory)
                continue;

            var path = record.Path.TrimEnd('\\', '/');
            foreach (var root in orderedRoots)
            {
                if (!IsUnderOrEqual(path, root))
                    continue;

                index[record.RecordNumber] = root;
                break;
            }
        }

        return index;
    }

    static bool IsUnderOrEqual(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            return true;

        return path.Length > root.Length
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (path[root.Length] == '\\' || path[root.Length] == '/');
    }
}
