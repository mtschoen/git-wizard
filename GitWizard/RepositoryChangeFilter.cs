using System.Diagnostics.CodeAnalysis;
using GitWizard.Watch;

namespace GitWizard;

/// <summary>
/// Result of <see cref="RepositoryChangeFilter.Classify"/>: tracked roots with modified
/// content, new repository roots discovered under a search root, tracked roots whose
/// repository was deleted, and untracked directories renamed into a search root
/// (<see cref="MovedInCandidates"/>, left for the service layer to probe for a .git entry -
/// this filter never touches the filesystem).
/// </summary>
public readonly record struct FilterResult(
    IReadOnlyCollection<string> Changed, IReadOnlyCollection<string> Created,
    IReadOnlyCollection<string> Deleted, IReadOnlyCollection<string> MovedInCandidates);

/// <summary>
/// Maps raw volume change batches back to tracked and search repository roots.
/// </summary>
/// <remarks>
/// I/O-free and cross-platform-testable: classification only consumes
/// <see cref="VolumeChangeEntry"/> records and never touches the filesystem, so it can be
/// exercised with synthetic entries on any OS. The tracked root set is mutable across a
/// watch run via <see cref="AddRepositoryRoot"/> and <see cref="RemoveRepositoryRoot"/>;
/// search roots are fixed at construction. Mutation is single-threaded (the watch pump
/// thread) and never runs concurrently with <see cref="Classify"/>.
/// </remarks>
public sealed class RepositoryChangeFilter
{
    readonly Dictionary<int, List<WatchPath>> _rootsByLength = new();
    readonly SortedSet<int> _descendingLengths = new(Comparer<int>.Create((a, b) => b.CompareTo(a)));
    readonly HashSet<string> _trackedRootsSet = new(WatchPath.StringComparer);
    readonly WatchPath[] _orderedSearchRoots;
    readonly WatchPath[] _orderedIgnoredPaths;
    readonly bool _includeTempDirectories;

    /// <summary>
    /// Build the filter for one volume.
    /// </summary>
    /// <param name="repositoryRootPaths">Tracked repository roots on that volume.</param>
    /// <param name="searchRoots">
    /// Configured discovery roots, used by <see cref="Classify"/> to recognize newly created
    /// repositories.
    /// </param>
    /// <param name="ignoredPaths">
    /// Configured ignored paths to exclude from classification. Any change entry or repository
    /// under an ignored path is skipped regardless of whether temp inclusion is enabled.
    /// </param>
    /// <param name="includeTempDirectories">When false (the default), a repository under a temp
    /// directory (<see cref="TempDirectories"/>) is not reported as changed or created,
    /// unless the containing search root is itself inside a temp directory.</param>
    public RepositoryChangeFilter(
        IReadOnlyCollection<string> repositoryRootPaths,
        IReadOnlyCollection<string>? searchRoots = null,
        IReadOnlyCollection<string>? ignoredPaths = null,
        bool includeTempDirectories = false)
    {
        foreach (var rootPath in repositoryRootPaths)
        {
            AddRepositoryRoot(rootPath);
        }

        _orderedSearchRoots = OrderRootsForClassify(searchRoots ?? []);
        _orderedIgnoredPaths = OrderRootsForClassify(ignoredPaths ?? []);
        _includeTempDirectories = includeTempDirectories;
    }

    public RepositoryChangeFilter(
        IReadOnlyCollection<string> repositoryRootPaths,
        IReadOnlyCollection<string>? searchRoots,
        bool includeTempDirectories)
        : this(repositoryRootPaths, searchRoots, null, includeTempDirectories)
    {
    }

    /// <summary>
    /// Classifies a single volume change entry without mutating the filter's tracked roots.
    /// Returns the classified change kind and matching repository root, or <see langword="null"/>
    /// if the entry is not relevant to any tracked or search root. A <see cref="VolumeEntryKind.MovedIn"/>
    /// entry that names a candidate for a new repository root (rather than a change under an
    /// already-tracked one) is not classified here - see <see cref="TryClassifyMovedInCandidate"/>.
    /// </summary>
    public (RepositoryChangeKind Kind, string Root)? ClassifyEntry(VolumeChangeEntry entry)
    {
        var path = WatchPath.Parse(entry.FullPath);
        if (IsIgnored(path))
        {
            return null;
        }

        switch (entry.Kind)
        {
            case VolumeEntryKind.Modified:
                {
                    var root = FindContainingTrackedRoot(path);
                    return root is not null && !IsExcludedTempPath(root.Value)
                        ? (RepositoryChangeKind.Changed, root.Value.Original)
                        : null;
                }
            case VolumeEntryKind.Created:
                {
                    var trackedRoot = FindContainingTrackedRoot(path);
                    if (trackedRoot is not null)
                    {
                        return !IsExcludedTempPath(trackedRoot.Value)
                            ? (RepositoryChangeKind.Changed, trackedRoot.Value.Original)
                            : null;
                    }

                    if (path.TryGetGitDirectoryParent(out var parent)
                        && FindContainingRoot(parent, _orderedSearchRoots) is not null
                        && !IsExcludedTempPath(parent))
                    {
                        return (RepositoryChangeKind.Created, parent.Original);
                    }

                    return null;
                }
            case VolumeEntryKind.Deleted:
                {
                    foreach (var length in _descendingLengths)
                    {
                        foreach (var root in _rootsByLength[length])
                        {
                            if (path.IsEqualTo(root) || path.IsEqualToChildOf(root, ".git"))
                            {
                                return !IsExcludedTempPath(root)
                                    ? (RepositoryChangeKind.Deleted, root.Original)
                                    : null;
                            }
                        }
                    }

                    var rootChanged = FindContainingTrackedRoot(path);
                    return rootChanged is not null && !IsExcludedTempPath(rootChanged.Value)
                        ? (RepositoryChangeKind.Changed, rootChanged.Value.Original)
                        : null;
                }
            case VolumeEntryKind.MovedIn:
                {
                    var candidate = path.TryGetGitDirectoryParent(out var parent) ? parent : path;
                    var trackedRoot = FindContainingTrackedRoot(candidate);
                    if (trackedRoot is not null)
                    {
                        // A case-only rename of a tracked root itself is a moved-in candidate
                        // (the service layer re-adds it under its new casing), not a content change.
                        if (candidate.IsEqualTo(trackedRoot.Value)
                            && !string.Equals(candidate.Original, trackedRoot.Value.Original, StringComparison.Ordinal))
                        {
                            return null;
                        }

                        return !IsExcludedTempPath(trackedRoot.Value)
                            ? (RepositoryChangeKind.Changed, trackedRoot.Value.Original)
                            : null;
                    }

                    return null;
                }
            default:
                return null;
        }
    }

    /// <summary>
    /// Checks whether <paramref name="entry"/> represents a moved-in repository root candidate
    /// (an untracked directory or .git marker renamed into a search root, or a case-only rename
    /// of an existing tracked root). If so, returns <see langword="true"/> and outputs the
    /// candidate path to be probed by the watch service at debounce flush. Ignored and excluded
    /// temp paths never produce a candidate.
    /// </summary>
    public bool TryClassifyMovedInCandidate(VolumeChangeEntry entry, [NotNullWhen(true)] out string? candidateRoot)
    {
        if (entry.Kind != VolumeEntryKind.MovedIn)
        {
            candidateRoot = null;
            return false;
        }

        var path = WatchPath.Parse(entry.FullPath);
        if (IsIgnored(path))
        {
            candidateRoot = null;
            return false;
        }

        var candidate = path.TryGetGitDirectoryParent(out var parent) ? parent : path;
        var trackedRoot = FindContainingTrackedRoot(candidate);
        if (trackedRoot is not null)
        {
            if (candidate.IsEqualTo(trackedRoot.Value)
                && !string.Equals(candidate.Original, trackedRoot.Value.Original, StringComparison.Ordinal))
            {
                candidateRoot = candidate.Original;
                return true;
            }

            candidateRoot = null;
            return false;
        }

        if (FindContainingRoot(candidate, _orderedSearchRoots) is not null
            && !IsExcludedTempPath(candidate))
        {
            candidateRoot = candidate.Original;
            return true;
        }

        candidateRoot = null;
        return false;
    }

    /// <summary>
    /// Classifies a batch of raw volume change entries into modified tracked roots, new
    /// repository roots discovered under a search root, tracked roots whose repository was
    /// deleted, and untracked moved-in candidates. Does not mutate the filter's tracked roots.
    /// </summary>
    public FilterResult Classify(IReadOnlyList<VolumeChangeEntry> entries)
    {
        var changed = new HashSet<string>(WatchPath.StringComparer);
        var created = new HashSet<string>(WatchPath.StringComparer);
        var deleted = new HashSet<string>(WatchPath.StringComparer);
        var movedInCandidates = new HashSet<string>(WatchPath.StringComparer);

        foreach (var entry in entries)
        {
            if (TryClassifyMovedInCandidate(entry, out var candidate))
            {
                movedInCandidates.Add(candidate);
                continue;
            }

            var classification = ClassifyEntry(entry);
            if (classification is null)
            {
                continue;
            }

            var (kind, root) = classification.Value;
            switch (kind)
            {
                case RepositoryChangeKind.Changed:
                    changed.Add(root);
                    break;
                case RepositoryChangeKind.Created:
                    created.Add(root);
                    break;
                case RepositoryChangeKind.Deleted:
                    deleted.Add(root);
                    break;
            }
        }

        return new FilterResult(changed, created, deleted, movedInCandidates);
    }

    bool IsIgnored(WatchPath path) =>
        FindContainingRoot(path, _orderedIgnoredPaths) is not null;

    bool IsExcludedTempPath(WatchPath path)
    {
        if (_includeTempDirectories)
        {
            return false;
        }

        if (!TempDirectories.IsTempPath(path.Original))
        {
            return false;
        }

        var searchRoot = FindContainingRoot(path, _orderedSearchRoots);
        return searchRoot is null || !TempDirectories.IsTempPath(searchRoot.Value.Original);
    }

    /// <summary>
    /// Adds <paramref name="rootPath"/> to the tracked repository roots mid-run. The watch
    /// service calls this when a repository is created under a search root, so later edits
    /// inside it classify as Changed without a watch restart. A root that is already tracked
    /// is not added twice. Bucketing roots by length avoids shifting an accumulated list
    /// of roots while preserving descending length precedence across distinct lengths.
    /// </summary>
    public void AddRepositoryRoot(string rootPath)
    {
        if (!_trackedRootsSet.Add(rootPath))
        {
            return;
        }

        var root = WatchPath.Parse(rootPath);
        var length = root.Comparison.Length;
        if (_rootsByLength.TryGetValue(length, out var list))
        {
            list.Add(root);
        }
        else
        {
            _descendingLengths.Add(length);
            _rootsByLength[length] = [root];
        }
    }

    /// <summary>
    /// Removes <paramref name="rootPath"/> from the tracked repository roots mid-run. The
    /// watch service calls this when a tracked repository's root or .git directory is
    /// deleted, so a later re-creation under a search root classifies as Created again
    /// instead of a Changed no consumer can apply. Removing an untracked root is a no-op.
    /// </summary>
    public void RemoveRepositoryRoot(string rootPath)
    {
        if (!_trackedRootsSet.Remove(rootPath))
        {
            return;
        }

        var root = WatchPath.Parse(rootPath);
        var length = root.Comparison.Length;
        if (!_rootsByLength.TryGetValue(length, out var list))
        {
            return;
        }

        for (var index = 0; index < list.Count; index++)
        {
            if (!list[index].IsEqualTo(root))
            {
                continue;
            }

            var lastIndex = list.Count - 1;
            if (index != lastIndex)
            {
                list[index] = list[lastIndex];
            }
            list.RemoveAt(lastIndex);

            if (list.Count == 0)
            {
                _rootsByLength.Remove(length);
                _descendingLengths.Remove(length);
            }
            return;
        }
    }

    WatchPath? FindContainingTrackedRoot(WatchPath path)
    {
        var pathLength = path.Comparison.Length;
        foreach (var length in _descendingLengths)
        {
            if (length > pathLength)
            {
                continue;
            }

            foreach (var root in _rootsByLength[length])
            {
                if (path.IsUnderOrEqual(root))
                {
                    return root;
                }
            }
        }

        return null;
    }

    static WatchPath? FindContainingRoot(WatchPath path, WatchPath[] orderedRoots)
    {
        var pathLength = path.Comparison.Length;
        foreach (var root in orderedRoots)
        {
            if (root.Comparison.Length > pathLength)
            {
                continue;
            }

            if (path.IsUnderOrEqual(root))
            {
                return root;
            }
        }

        return null;
    }

    // Classify path handling goes through WatchPath, which retains each path's syntax
    // (Windows-rooted vs POSIX) rather than rewriting separators. A '/' to '\\' rewrite would
    // report a Linux repository root back to consumers as "\repos\repo-a".
    static WatchPath[] OrderRootsForClassify(IReadOnlyCollection<string> roots) =>
        [.. roots.Select(WatchPath.Parse).OrderByDescending(root => root.Comparison.Length)];
}
