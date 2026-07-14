using GitWizard.Watch;
using MFTLib;

namespace GitWizard;

/// <summary>
/// Result of <see cref="RepositoryChangeFilter.Classify"/>: tracked roots with modified
/// content, new repository roots discovered under a search root, and tracked roots whose
/// repository was deleted.
/// </summary>
public readonly record struct FilterResult(
    IReadOnlyCollection<string> Changed, IReadOnlyCollection<string> Created, IReadOnlyCollection<string> Deleted);

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
    readonly string[] _orderedRepositoryRoots;
    readonly string[] _orderedSearchRoots;

    /// <summary>
    /// Build the filter's record-number index for one drive.
    /// </summary>
    /// <param name="scanRecords">Cold-scan records for one drive.</param>
    /// <param name="repositoryRootPaths">Tracked repository roots on that same drive.</param>
    /// <param name="searchRoots">
    /// Configured discovery roots, used by <see cref="Classify"/> to recognize newly created
    /// repositories. Optional so the pre-existing <see cref="Filter"/> call site keeps compiling.
    /// </param>
    public RepositoryChangeFilter(
        IReadOnlyList<ScanRecord> scanRecords,
        IReadOnlyCollection<string> repositoryRootPaths,
        IReadOnlyCollection<string>? searchRoots = null)
    {
        _repositoryRootByRecordNumber = BuildIndex(scanRecords, repositoryRootPaths);
        _orderedRepositoryRoots = OrderRootsForClassify(repositoryRootPaths);
        _orderedSearchRoots = OrderRootsForClassify(searchRoots ?? []);
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

    /// <summary>
    /// Classifies a batch of raw volume change entries into modified tracked roots, new
    /// repository roots discovered under a search root, and tracked roots whose repository
    /// was deleted.
    /// </summary>
    public FilterResult Classify(IReadOnlyList<VolumeChangeEntry> entries)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var path = NormalizeClassifyPath(entry.FullPath);
            switch (entry.Kind)
            {
                case VolumeEntryKind.Modified:
                    ClassifyChanged(changed, path);
                    break;
                case VolumeEntryKind.Created:
                    ClassifyCreated(created, path);
                    break;
                case VolumeEntryKind.Deleted:
                    ClassifyDeleted(deleted, path);
                    break;
            }
        }

        return new FilterResult(changed, created, deleted);
    }

    void ClassifyChanged(HashSet<string> changed, string path)
    {
        var root = FindContainingRoot(path, _orderedRepositoryRoots);
        if (root is not null)
            changed.Add(root);
    }

    void ClassifyCreated(HashSet<string> created, string path)
    {
        if (!TryGetGitDirectoryParent(path, out var parent))
            return;

        if (FindContainingRoot(parent, _orderedSearchRoots) is not null)
            created.Add(parent);
    }

    void ClassifyDeleted(HashSet<string> deleted, string path)
    {
        foreach (var root in _orderedRepositoryRoots)
        {
            var isRootItself = path.Equals(root, StringComparison.OrdinalIgnoreCase);
            var isRootGitDirectory = path.Equals(root + @"\.git", StringComparison.OrdinalIgnoreCase);
            if (isRootItself || isRootGitDirectory)
            {
                deleted.Add(root);
                break;
            }
        }
    }

    static string? FindContainingRoot(string path, IReadOnlyList<string> orderedRoots)
    {
        foreach (var root in orderedRoots)
        {
            if (IsUnderOrEqual(path, root))
                return root;
        }

        return null;
    }

    static bool TryGetGitDirectoryParent(string path, out string parent)
    {
        var separatorIndex = path.LastIndexOf('\\');
        var name = separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
        if (separatorIndex < 0 || !name.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            parent = "";
            return false;
        }

        parent = path[..separatorIndex];
        return true;
    }

    // Longest-root-first so a nested repository root wins the mapping for directories under
    // it, rather than the outer repository that also contains it. Roots and scan-record
    // paths are both trailing-trimmed only (no separator rewrite) so they compare
    // symmetrically regardless of internal separator style.
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

    static string[] OrderRootsForClassify(IReadOnlyCollection<string> roots) =>
        roots.Select(NormalizeClassifyPath).OrderByDescending(root => root.Length).ToArray();

    // Classify-only separator rewrite (Linux fanotify '/' and Windows USN '\\' unified); the
    // legacy record-number Filter/BuildIndex path deliberately does NOT use this.
    static string NormalizeClassifyPath(string path) => path.Replace('/', '\\').TrimEnd('\\');
}
