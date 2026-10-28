using GitWizard.Discovery;
using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard;

public static partial class GitWizardApi
{
    internal const string GitEntryName = ".git";

    internal static IReadOnlyList<FileEntry> SelectGitEntries(
        FileIndexSet index, bool includeGitFiles, CancellationToken cancellationToken = default)
    {
        return (FileEntry[])[.. index.Search(new SearchQuery(GitEntryName, NameMatchMode.Exact), cancellationToken)
            .Where(entry => !entry.IsDeleted && (includeGitFiles || (TryReadIsDirectory(entry, out var isDirectory) && isDirectory)))];
    }

    /// <summary>
    /// Selects ordinary, gitfile, and bare repository candidates from the index matching the discovery scope.
    /// </summary>
    public static IReadOnlyList<RepositoryCandidate> SelectRepositoryCandidates(
        FileIndexSet index, DiscoveryScope scope, CancellationToken cancellationToken = default) =>
        SelectRepositoryCandidates(index, scope, null, cancellationToken);

    internal static IReadOnlyList<RepositoryCandidate> SelectRepositoryCandidates(
        FileIndexSet index, DiscoveryScope scope, CandidateSelectionDiagnostics? diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        scope = ResolvedDiscoveryScope.Create(index, scope);
        var results = new List<RepositoryCandidate>();
        var seenPaths = new HashSet<string>(WatchPath.StringComparer);

        foreach (var member in index.Indices.Where(member => member.Status.Block is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Each member's queries are already restricted to its directory view by MFTLib.
            // The remaining scope predicates are candidate admission (including exact retained roots).
            var candidates = SelectRootCandidates(new FileIndexSet([member]), scope, diagnostics, cancellationToken);
            results.AddRange(candidates.Where(candidate => seenPaths.Add(candidate.Path)));
        }

        return results.AsReadOnly();
    }

    // Every index scan the selection starts goes through here so a test can count them. The result is
    // materialized because each candidate scope reads it again.
    internal static IReadOnlyList<FileEntry> ScanByName(
        FileIndexSet index, string name, CandidateSelectionDiagnostics? diagnostics, CancellationToken cancellationToken)
    {
        diagnostics?.RecordIndexScan();
        return (FileEntry[])[.. index.Search(new SearchQuery(name, NameMatchMode.Exact), cancellationToken)];
    }

    internal readonly record struct CandidateScope(
        WatchPath TranslatedRoot,
        bool IsExact,
        bool CoversDriveRoot,
        CandidateAdmissionPolicy Policy);

    static IEnumerable<RepositoryCandidate> SelectRootCandidates(FileIndexSet index, DiscoveryScope scope,
        CandidateSelectionDiagnostics? diagnostics, CancellationToken cancellationToken)
    {
        var gitEntries = ScanByName(index, GitEntryName, diagnostics, cancellationToken);
        var headEntries = ScanByName(index, "HEAD", diagnostics, cancellationToken);
        var layouts = new Lazy<BareLayoutIndex>(() => BareLayoutIndex.Build(index, diagnostics, cancellationToken));
        var roots = scope.SearchRoots.Select(root => (Root: root, IsExact: false)).ToList();
        roots.AddRange(scope.RetainedRoots
            .Select(retainedRoot => (Root: retainedRoot, Path: WatchPath.Parse(retainedRoot)))
            .Where(retained => !scope.SearchRoots.Any(searchRoot => retained.Path.IsUnderOrEqual(WatchPath.Parse(searchRoot))))
            .Select(retained => (retained.Root, true)));

        var policy = CandidateAdmissionPolicy.ForScope(scope);
        foreach (var item in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = WatchPath.Parse(item.Root);
            var admission = new CandidateScope(
                root,
                item.IsExact,
                !item.IsExact && IsLogicalDriveRoot(root),
                policy);
            foreach (var candidate in SelectGitMarkerCandidates(gitEntries, admission, diagnostics)
                         .Concat(SelectBareCandidates(headEntries, admission, layouts, diagnostics)))
            {
                yield return candidate;
            }
        }
    }

    // A scope root is a logical Windows path even when the index was enumerated on another host, so the
    // host-dependent IsDriveRoot alone would miss "X:/" on Linux.
    static bool IsLogicalDriveRoot(WatchPath root) =>
        IsDriveRoot(root.Original) ||
        (root.IsWindows && root.Comparison is [_, ':', '/']);

    static IEnumerable<RepositoryCandidate> SelectGitMarkerCandidates(
        IEnumerable<FileEntry> gitEntries, CandidateScope candidateScope, CandidateSelectionDiagnostics? diagnostics)
    {
        foreach (var entry in gitEntries)
        {
            if (entry.IsDeleted || !TryReadIsDirectory(entry, out var isDirectory))
            {
                continue;
            }

            if (candidateScope.CoversDriveRoot && !isDirectory)
            {
                continue;
            }

            if (!TryGetScopedParent(entry, candidateScope, out _, out var candidate, diagnostics))
            {
                continue;
            }

            var kind = isDirectory ? RepositoryCandidateKind.Ordinary : RepositoryCandidateKind.GitFile;
            yield return new RepositoryCandidate(candidate.Original, kind);
        }
    }

    static IEnumerable<RepositoryCandidate> SelectBareCandidates(
        IEnumerable<FileEntry> headEntries, CandidateScope candidateScope, Lazy<BareLayoutIndex> layouts,
        CandidateSelectionDiagnostics? diagnostics)
    {
        foreach (var entry in headEntries)
        {
            if (entry.IsDeleted || !TryReadIsDirectory(entry, out var isDirectory) || isDirectory)
            {
                continue;
            }

            if (!TryGetScopedParent(entry, candidateScope, out var parent, out var candidate, diagnostics))
            {
                continue;
            }

            if (candidate.Comparison.Split('/').Any(segment =>
                    string.Equals(segment, GitEntryName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!layouts.Value.Contains(parent))
            {
                continue;
            }

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
        FileEntry entry, CandidateScope candidateScope, out FileEntry parent, out WatchPath candidate,
        CandidateSelectionDiagnostics? diagnostics = null)
    {
        candidate = default;
        parent = default;
        string parentPath;
        try
        {
            if (entry.Parent is not { IsDirectory: true } entryParent)
            {
                return false;
            }

            parent = entryParent;
            parentPath = parent.Path;
        }
        catch (InvalidDataException)
        {
            // The parent chain is longer than the index can name, so this candidate is unobservable,
            // which is different from a handle superseded mid-read.
            diagnostics?.RecordUnresolvedPath();
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        candidate = WatchPath.Parse(parentPath);
        var root = candidateScope.TranslatedRoot;
        var matches = candidateScope.IsExact ? candidate.IsEqualTo(root) : candidate.IsUnderOrEqual(root);
        var candidateDirectory = parent;
        return matches &&
               candidateScope.Policy.AdmitsWithin(
                   root, candidate, depth => HasHiddenAttributeWithin(candidateDirectory, depth));
    }

    // The nearest `depth` directories, starting at the candidate directory itself, are the ones
    // between the search root and the candidate. A directory superseded mid-read has no attribute.
    static bool HasHiddenAttributeWithin(FileEntry directory, int depth)
    {
        try
        {
            var current = directory;
            for (var level = 0; level < depth; level++)
            {
                if (CandidateAdmissionPolicy.HasHiddenAttribute(current.Attributes))
                {
                    return true;
                }

                if (current.Parent is not { } next)
                {
                    return false;
                }

                current = next;
            }

            return false;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            return false;
        }
    }

}
