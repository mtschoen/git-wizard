using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard;

public static partial class GitWizardApi
{
    internal static IReadOnlyList<FileEntry> SelectGitEntries(
        FileIndex index, bool includeGitFiles, CancellationToken cancellationToken = default)
    {
        return index.Search(new SearchQuery(GitEntryName, NameMatchMode.Exact), cancellationToken)
            .Where(entry => !entry.IsDeleted && (includeGitFiles || (TryReadIsDirectory(entry, out var isDirectory) && isDirectory)))
            .ToArray();
    }

    /// <summary>
    /// Selects ordinary, gitfile, and bare repository candidates from the index matching the discovery scope.
    /// </summary>
    public static IReadOnlyList<RepositoryCandidate> SelectRepositoryCandidates(
        FileIndex index, DiscoveryScope scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<RepositoryCandidate>();
        var seenPaths = new HashSet<string>(WatchPath.StringComparer);

        var gitEntries = index.Search(new SearchQuery(GitEntryName, NameMatchMode.Exact), cancellationToken);
        var headEntries = index.Search(new SearchQuery("HEAD", NameMatchMode.Exact), cancellationToken);

        foreach (var candidateScope in BuildCandidateScopes(index, scope))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scopeCandidates = SelectGitMarkerCandidates(gitEntries, candidateScope)
                .Concat(SelectBareCandidates(headEntries, candidateScope, cancellationToken));
            foreach (var candidate in scopeCandidates)
            {
                if (seenPaths.Add(candidate.Path))
                    results.Add(candidate);
            }
        }

        return results.AsReadOnly();
    }

    internal readonly record struct CandidateScope(
        WatchPath TranslatedRoot,
        bool IsExact,
        bool CoversDriveRoot,
        bool ExcludeTempDirectories,
        bool SkipHiddenDirectories,
        WatchPath[] IgnoredRoots);

    static List<CandidateScope> BuildCandidateScopes(FileIndex index, DiscoveryScope scope)
    {
        var roots = scope.SearchRoots.Select(root => (Root: root, IsExact: false)).ToList();
        foreach (var retainedRoot in scope.RetainedRoots)
        {
            var retained = WatchPath.Parse(retainedRoot);
            if (!scope.SearchRoots.Any(searchRoot => retained.IsUnderOrEqual(WatchPath.Parse(searchRoot))))
                roots.Add((retainedRoot, true));
        }

        var configuration = scope.Request.Configuration;
        var ignoredRoots = scope.IgnoredRoots
            .Select(ignoredRoot => TranslateToRealRoot(index, WatchPath.Parse(ignoredRoot)))
            .ToArray();
        return roots.Select(item =>
        {
            var root = WatchPath.Parse(item.Root);
            return new CandidateScope(
                TranslateToRealRoot(index, root),
                item.IsExact,
                !item.IsExact && IsLogicalDriveRoot(root),
                configuration.IncludeTempDirectories != true && !TempDirectories.IsTempPath(root.Original),
                configuration.SkipHiddenDirectories == true,
                ignoredRoots);
        }).ToList();
    }

    // A scope root is a logical Windows path even when the index was enumerated on another host, so the
    // host-dependent IsDriveRoot alone would miss "X:/" on Linux.
    static bool IsLogicalDriveRoot(WatchPath root) =>
        IsDriveRoot(root.Original) ||
        (root.IsWindows && root.Comparison is [_, ':', '/']);

    static IEnumerable<RepositoryCandidate> SelectGitMarkerCandidates(
        IEnumerable<FileEntry> gitEntries, CandidateScope candidateScope)
    {
        foreach (var entry in gitEntries)
        {
            if (entry.IsDeleted || !TryReadIsDirectory(entry, out var isDirectory))
                continue;
            if (candidateScope.CoversDriveRoot && !isDirectory)
                continue;
            if (!TryGetScopedParent(entry, candidateScope, out _, out var candidate))
                continue;

            var kind = isDirectory ? RepositoryCandidateKind.Ordinary : RepositoryCandidateKind.GitFile;
            yield return new RepositoryCandidate(candidate.Original, kind);
        }
    }

    static IEnumerable<RepositoryCandidate> SelectBareCandidates(
        IEnumerable<FileEntry> headEntries, CandidateScope candidateScope, CancellationToken cancellationToken)
    {
        foreach (var entry in headEntries)
        {
            if (entry.IsDeleted || !TryReadIsDirectory(entry, out var isDirectory) || isDirectory)
                continue;
            if (!TryGetScopedParent(entry, candidateScope, out var parent, out var candidate))
                continue;
            if (candidate.Comparison.Split('/').Any(segment =>
                    string.Equals(segment, GitEntryName, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!HasBareRepositoryLayout(parent, cancellationToken))
                continue;

            yield return new RepositoryCandidate(candidate.Original, RepositoryCandidateKind.Bare);
        }
    }

    // A handle whose row slot a newer file reused reads IsDeleted, but a handle can also be superseded
    // between that check and a column read. Such a read throws InvalidOperationException, and the
    // entry is gone, so the callers skip it.
    internal static bool TryReadIsDirectory(FileEntry entry, out bool isDirectory)
    {
        try
        {
            isDirectory = entry.IsDirectory;
            return true;
        }
        catch (InvalidOperationException)
        {
            isDirectory = false;
            return false;
        }
    }

    internal static bool TryGetScopedParent(
        FileEntry entry, CandidateScope candidateScope, out FileEntry parent, out WatchPath candidate)
    {
        candidate = default;
        parent = default;
        string parentPath;
        try
        {
            if (entry.Parent is not { IsDirectory: true } entryParent)
                return false;

            parent = entryParent;
            parentPath = parent.Path;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            return false;
        }

        candidate = WatchPath.Parse(parentPath);
        var root = candidateScope.TranslatedRoot;
        var matches = candidateScope.IsExact ? candidate.IsEqualTo(root) : candidate.IsUnderOrEqual(root);
        if (!matches || candidateScope.IgnoredRoots.Any(candidate.IsUnderOrEqual))
            return false;
        if (candidateScope.ExcludeTempDirectories && TempDirectories.IsTempPath(candidate.Original))
            return false;

        return !candidateScope.SkipHiddenDirectories ||
               candidate.Comparison.Length <= root.Comparison.Length ||
               !candidate.Comparison[root.Comparison.Length..].Split('/').Any(segment => segment.StartsWith('.'));
    }

    static bool HasBareRepositoryLayout(FileEntry directory, CancellationToken cancellationToken)
    {
        try
        {
            var children = directory.Children(cancellationToken);
            return HasChildDirectory(children, "objects") && HasChildDirectory(children, "refs");
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    static bool HasChildDirectory(IEnumerable<FileEntry> children, string name) =>
        children.Any(child => !child.IsDeleted &&
                              TryReadIsDirectory(child, out var isDirectory) && isDirectory &&
                              string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Rewrites a drive-letter-rooted path onto that drive's real indexed root directory, so it can
    /// be compared against <see cref="FileEntry.Path" /> values in the same coordinate space. Falls
    /// back to the original path for anything that is not a single-drive-letter Windows path, or
    /// whose drive is not part of <paramref name="index" /> (the caller's own scope filtering then
    /// naturally excludes it).
    /// </summary>
    static WatchPath TranslateToRealRoot(FileIndex index, WatchPath path)
    {
        if (!path.IsWindows || path.Volume is not { Length: 1 } volume || !char.IsAsciiLetter(volume[0]))
            return path;
        string realRoot;
        try
        {
            realRoot = index.Root(char.ToUpperInvariant(volume[0])).Path;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            // Not every configured search root's drive is necessarily part of this index (a
            // denied or failed drive is reported through ScanErrors and never opened), so a
            // drive FileIndex.Root rejects is treated the same as one it silently has no
            // entries for: the caller's own scope filtering excludes it naturally.
            return path;
        }
        var realRootPath = WatchPath.Parse(realRoot);
        if (path.IsUnderOrEqual(realRootPath))
            return path;
        if (realRootPath.IsWindows && string.Equals(realRootPath.Volume, volume, StringComparison.OrdinalIgnoreCase))
            return path;

        var relative = path.Comparison.Length > 3 ? path.Comparison[3..] : string.Empty;
        return WatchPath.Parse(relative.Length == 0
            ? realRoot
            : Path.Combine(realRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
    }
}
