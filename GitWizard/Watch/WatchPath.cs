namespace GitWizard.Watch;

/// <summary>
/// Captures a raw watch path together with the syntax and comparison form derived from it.
/// Syntax is retained explicitly so normalization cannot turn Windows-rooted paths into
/// paths that are later mistaken for POSIX paths.
/// </summary>
readonly record struct WatchPath
{
    WatchPath(string original, string comparison, bool isWindows, string? volume)
    {
        Original = original;
        Comparison = comparison;
        IsWindows = isWindows;
        Volume = volume;
    }

    public static IEqualityComparer<string> StringComparer { get; } = new PathShapeComparer();

    public string Original { get; }
    public string Comparison { get; }
    public bool IsWindows { get; }
    public string? Volume { get; }

    StringComparison ComparisonKind =>
        IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static WatchPath Parse(string rawPath)
    {
        var isWindows = IsWindowsSyntax(rawPath);
        var original = TrimTrailingSeparators(rawPath, isWindows);
        var comparison = isWindows ? original.Replace('\\', '/') : original;
        return new WatchPath(original, comparison, isWindows, FindVolume(original, isWindows));
    }

    public bool IsEqualTo(WatchPath other) =>
        IsWindows == other.IsWindows
        && Comparison.Equals(other.Comparison, ComparisonKind);

    public bool IsUnderOrEqual(WatchPath root)
    {
        if (IsWindows != root.IsWindows)
        {
            return false;
        }

        if (Comparison.Equals(root.Comparison, root.ComparisonKind))
        {
            return true;
        }

        if (root.Comparison.EndsWith('/'))
        {
            return Comparison.StartsWith(root.Comparison, root.ComparisonKind);
        }

        return Comparison.Length > root.Comparison.Length
            && Comparison.StartsWith(root.Comparison, root.ComparisonKind)
            && Comparison[root.Comparison.Length] == '/';
    }

    public bool IsEqualToChildOf(WatchPath parent, string childName)
    {
        if (IsWindows != parent.IsWindows)
        {
            return false;
        }

        var childComparison = parent.Comparison.EndsWith('/')
            ? parent.Comparison + childName
            : parent.Comparison + "/" + childName;
        return Comparison.Equals(childComparison, parent.ComparisonKind);
    }

    public bool TryGetGitDirectoryParent(out WatchPath parent)
    {
        var separatorIndex = IsWindows
            ? Math.Max(Original.LastIndexOf('\\'), Original.LastIndexOf('/'))
            : Original.LastIndexOf('/');
        var name = separatorIndex < 0 ? Original : Original[(separatorIndex + 1)..];
        if (separatorIndex < 0 || !name.Equals(".git", ComparisonKind))
        {
            parent = default;
            return false;
        }

        var parentLength = separatorIndex;
        if (separatorIndex == 0 || IsDriveRootSeparator(separatorIndex))
        {
            parentLength++;
        }

        parent = Parse(Original[..parentLength]);
        return true;
    }

    static bool IsWindowsSyntax(string path)
    {
        if (path.StartsWith('/'))
        {
            return false;
        }

        return IsDrivePath(path) || path.Contains('\\');
    }

    static string TrimTrailingSeparators(string path, bool isWindows)
    {
        if (path == "/" || path == @"\" || IsDriveRoot(path))
        {
            return path;
        }

        return isWindows ? path.TrimEnd('\\', '/') : path.TrimEnd('/');
    }

    static string? FindVolume(string path, bool isWindows)
    {
        if (IsDrivePath(path))
        {
            return path[..1];
        }

        if (!isWindows)
        {
            return path.StartsWith('/') ? "/" : null;
        }

        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return path.StartsWith('\\') ? @"\" : null;
        }

        var serverSeparator = FindSeparator(path, 2);
        if (serverSeparator < 0)
        {
            return path;
        }

        var shareSeparator = FindSeparator(path, serverSeparator + 1);
        return shareSeparator < 0 ? path : path[..shareSeparator];
    }

    static int FindSeparator(string path, int startIndex)
    {
        for (var index = startIndex; index < path.Length; index++)
        {
            if (path[index] is '\\' or '/')
            {
                return index;
            }
        }

        return -1;
    }

    static bool IsDriveRoot(string path) =>
        path.Length == 3 && IsDrivePath(path) && path[2] is '\\' or '/';

    bool IsDriveRootSeparator(int separatorIndex) =>
        separatorIndex == 2 && IsDrivePath(Original);

    static bool IsDrivePath(string path) =>
        path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]);

    sealed class PathShapeComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            return Parse(x).IsEqualTo(Parse(y));
        }

        public int GetHashCode(string obj)
        {
            var path = Parse(obj);
            var comparer = path.IsWindows
                ? System.StringComparer.OrdinalIgnoreCase
                : System.StringComparer.Ordinal;
            return HashCode.Combine(path.IsWindows, comparer.GetHashCode(path.Comparison));
        }
    }
}
