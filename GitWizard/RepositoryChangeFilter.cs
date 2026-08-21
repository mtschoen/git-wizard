using GitWizard.Watch;

namespace GitWizard;

/// <summary>
/// Result of <see cref="RepositoryChangeFilter.Classify"/>: tracked roots with modified
/// content, new repository roots discovered under a search root, and tracked roots whose
/// repository was deleted.
/// </summary>
public readonly record struct FilterResult(
    IReadOnlyCollection<string> Changed, IReadOnlyCollection<string> Created, IReadOnlyCollection<string> Deleted);

/// <summary>
/// Maps raw volume change batches back to tracked and search repository roots.
/// </summary>
/// <remarks>
/// Pure and cross-platform-testable: it only consumes <see cref="VolumeChangeEntry"/> records
/// and never touches the filesystem, so it can be exercised with synthetic entries on any OS.
/// </remarks>
public sealed class RepositoryChangeFilter
{
    readonly WatchPath[] _orderedRepositoryRoots;
    readonly WatchPath[] _orderedSearchRoots;

    /// <summary>
    /// Build the filter for one volume.
    /// </summary>
    /// <param name="repositoryRootPaths">Tracked repository roots on that volume.</param>
    /// <param name="searchRoots">
    /// Configured discovery roots, used by <see cref="Classify"/> to recognize newly created
    /// repositories.
    /// </param>
    public RepositoryChangeFilter(
        IReadOnlyCollection<string> repositoryRootPaths,
        IReadOnlyCollection<string>? searchRoots = null)
    {
        _orderedRepositoryRoots = OrderRootsForClassify(repositoryRootPaths);
        _orderedSearchRoots = OrderRootsForClassify(searchRoots ?? []);
    }

    /// <summary>
    /// Classifies a batch of raw volume change entries into modified tracked roots, new
    /// repository roots discovered under a search root, and tracked roots whose repository
    /// was deleted.
    /// </summary>
    public FilterResult Classify(IReadOnlyList<VolumeChangeEntry> entries)
    {
        var changed = new HashSet<string>(WatchPath.StringComparer);
        var created = new HashSet<string>(WatchPath.StringComparer);
        var deleted = new HashSet<string>(WatchPath.StringComparer);

        foreach (var entry in entries)
        {
            var path = WatchPath.Parse(entry.FullPath);
            switch (entry.Kind)
            {
                case VolumeEntryKind.Modified:
                    ClassifyChanged(changed, path);
                    break;
                case VolumeEntryKind.Created:
                    ClassifyCreated(changed, created, path);
                    break;
                case VolumeEntryKind.Deleted:
                    ClassifyDeleted(changed, deleted, path);
                    break;
            }
        }

        return new FilterResult(changed, created, deleted);
    }

    void ClassifyChanged(HashSet<string> changed, WatchPath path)
    {
        var root = FindContainingRoot(path, _orderedRepositoryRoots);
        if (root is not null)
            changed.Add(root.Value.Original);
    }

    void ClassifyCreated(HashSet<string> changed, HashSet<string> created, WatchPath path)
    {
        var trackedRoot = FindContainingRoot(path, _orderedRepositoryRoots);
        if (trackedRoot is not null)
        {
            changed.Add(trackedRoot.Value.Original);
            return;
        }

        if (path.TryGetGitDirectoryParent(out var parent)
            && FindContainingRoot(parent, _orderedSearchRoots) is not null)
            created.Add(parent.Original);
    }

    void ClassifyDeleted(HashSet<string> changed, HashSet<string> deleted, WatchPath path)
    {
        foreach (var root in _orderedRepositoryRoots)
        {
            if (path.IsEqualTo(root) || path.IsEqualToChildOf(root, ".git"))
            {
                deleted.Add(root.Original);
                return;
            }
        }

        ClassifyChanged(changed, path);
    }

    static WatchPath? FindContainingRoot(WatchPath path, IReadOnlyList<WatchPath> orderedRoots)
    {
        foreach (var root in orderedRoots)
        {
            if (path.IsUnderOrEqual(root))
                return root;
        }

        return null;
    }

    // Classify path handling goes through WatchPath, which retains each path's syntax
    // (Windows-rooted vs POSIX) rather than rewriting separators. A '/' to '\\' rewrite would
    // report a Linux repository root back to consumers as "\repos\repo-a".
    static WatchPath[] OrderRootsForClassify(IReadOnlyCollection<string> roots) =>
        roots.Select(WatchPath.Parse)
            .OrderByDescending(root => root.Comparison.Length)
            .ToArray();
}
