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
    internal static IEnumerable<string> ResolveRepositoryRootCandidates(IEnumerable<FileEntry> entries,
        string rootPath, ICollection<string> ignoredPaths, bool? skipHiddenDirectories = null)
    {
        var root = WatchPath.Parse(rootPath);
        if (!root.IsWindows)
            yield break;
        var includeGitFiles = root.Comparison.Length != 3 || root.Comparison[1] != ':' || root.Comparison[2] != '/';
        var ignoredRoots = ignoredPaths.Select(WatchPath.Parse).ToArray();
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
            if (!candidate.IsUnderOrEqual(root) || ignoredRoots.Any(candidate.IsUnderOrEqual))
                continue;
            if (skipHiddenDirectories != false && candidate.Comparison[root.Comparison.Length..]
                .Split('/').Any(segment => segment.StartsWith('.')))
                continue;
            yield return candidate.Original;
        }
    }
}
