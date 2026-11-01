using System.Runtime.InteropServices;

namespace GitWizard;

public static partial class GitWizardApi
{
    /// <summary>
    /// Returns the absolute form of <paramref name="path"/> without trailing separators and with the
    /// casing the filesystem stores. Windows matches names case-insensitively, so a typed path may differ
    /// in case from the directory; published paths carry the stored casing and comparisons go through
    /// <see cref="NormalizePath"/> or <see cref="Watch.WatchPath.StringComparer"/>. Segments that do not
    /// exist or cannot be listed keep the casing they were given.
    /// </summary>
    internal static string ResolveOnDiskPath(string path)
    {
        var trimmed = TrimTrailingSeparators(Path.GetFullPath(path));
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return trimmed;
        }

        var root = Path.GetPathRoot(trimmed) ?? string.Empty;
        if (root.Length >= 2 && root[1] == ':')
        {
            root = char.ToUpperInvariant(root[0]) + root[1..];
        }

        var resolved = root;
        var segments = trimmed[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var stored = FindStoredName(resolved, segments[index]);
            if (stored is null)
            {
                return Path.Combine([resolved, .. segments[index..]]);
            }

            resolved = Path.Combine(resolved, stored);
        }

        return resolved;
    }

    static string TrimTrailingSeparators(string fullPath)
    {
        // Preserve drive root paths (e.g. "C:\") because "C:" without a separator is relative on Windows.
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length > 1 && fullPath != root
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
    }

    static string? FindStoredName(string parent, string name)
    {
        try
        {
            return new DirectoryInfo(parent).EnumerateFileSystemInfos(name).FirstOrDefault()?.Name;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
