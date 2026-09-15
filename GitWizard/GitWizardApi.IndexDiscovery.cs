using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard;

public static partial class GitWizardApi
{
    internal static IReadOnlyList<FileEntry> SelectGitEntries(FileIndex index, bool includeGitFiles)
    {
        return index.FindByName(GitEntryName)
            .Where(entry => !entry.IsDeleted && (includeGitFiles || entry.IsDirectory))
            .ToArray();
    }

    /// <summary>Applies logical Windows scope policy without opening an indexed path on disk.</summary>
    /// <remarks>
    /// <paramref name="index" /> resolves each drive-letter-rooted path (<paramref name="rootPath" />
    /// and <paramref name="ignoredPaths" />) onto that drive's real indexed root directory before
    /// comparing against <see cref="FileEntry.Path" />, because <c>FileEntry.Path</c> renders the
    /// real root directory rather than a synthetic <c>&lt;letter&gt;:\</c> path when
    /// <c>IndexedDrive.RootDirectory</c> differs from the drive letter (MFTLib #143). On a real
    /// Windows volume the two coincide, so this is a no-op there.
    /// </remarks>
    internal static IEnumerable<string> ResolveRepositoryRootCandidates(FileIndex index, IEnumerable<FileEntry> entries,
        string rootPath, ICollection<string> ignoredPaths, bool? skipHiddenDirectories = null)
    {
        var root = WatchPath.Parse(rootPath);
        if (!root.IsWindows)
            yield break;
        var includeGitFiles = root.Comparison.Length != 3 || root.Comparison[1] != ':' || root.Comparison[2] != '/';
        var translatedRoot = TranslateToRealRoot(index, root);
        var ignoredRoots = ignoredPaths.Select(WatchPath.Parse)
            .Select(ignoredRoot => TranslateToRealRoot(index, ignoredRoot)).ToArray();
        foreach (var entry in entries)
        {
            if (entry.IsDeleted || (!includeGitFiles && !entry.IsDirectory) || entry.Parent is not { } parent)
                continue;
            string parentPath;
            try
            {
                parentPath = parent.Path;
            }
            catch (InvalidDataException)
            {
                continue;
            }
            var candidate = WatchPath.Parse(parentPath);
            if (!candidate.IsUnderOrEqual(translatedRoot) || ignoredRoots.Any(candidate.IsUnderOrEqual))
                continue;
            if (skipHiddenDirectories != false && candidate.Comparison[translatedRoot.Comparison.Length..]
                .Split('/').Any(segment => segment.StartsWith('.')))
                continue;
            yield return candidate.Original;
        }
    }

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
        var relative = path.Comparison.Length > 3 ? path.Comparison[3..] : string.Empty;
        var translated = relative.Length == 0
            ? realRoot
            : Path.Combine(realRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        return WatchPath.Parse(translated);
    }
}
