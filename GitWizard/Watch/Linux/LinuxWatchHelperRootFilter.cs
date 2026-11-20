namespace GitWizard.Watch.Linux;

/// <summary>
/// Decides which filesystem-wide entries can affect the armed roots: an entry at a root, under one,
/// or directly beside one (a root renamed or replaced within its parent). Lookups are hash-set
/// membership over the entry's path components, so an unrelated entry costs its path depth and not
/// the number of armed roots.
/// </summary>
sealed class LinuxWatchHelperRootFilter
{
    readonly HashSet<string> _roots;
    readonly HashSet<string> _rootParents;

    public LinuxWatchHelperRootFilter(IReadOnlyList<string> roots)
    {
        // The comparer parses each key, so POSIX paths stay case-sensitive and trailing separators
        // never distinguish two spellings of one path.
        _roots = new HashSet<string>(roots, WatchPath.StringComparer);
        _rootParents = new HashSet<string>(roots.Select(Path.GetDirectoryName).OfType<string>(), WatchPath.StringComparer);
    }

    public bool IsRelevant(string fullPath) =>
        IsAtOrUnderRoot(WatchPath.Parse(fullPath)) ||
        (Path.GetDirectoryName(fullPath) is { } directory && _rootParents.Contains(directory));

    // Walks up by path component, never by string prefix, so /x/Repos2 is not under /x/Repos.
    bool IsAtOrUnderRoot(WatchPath path)
    {
        // A backslash is a separator only in Windows syntax; in a POSIX path it is part of a name.
        var candidate = path.Original;
        while (true)
        {
            if (_roots.Contains(candidate))
            {
                return true;
            }

            var separator = path.IsWindows ? candidate.LastIndexOfAny(['/', '\\']) : candidate.LastIndexOf('/');
            if (separator < 0)
            {
                return false;
            }

            // A leading separator or a drive root keeps its separator: "/" and "C:\" are directories.
            var keepsSeparator = separator == 0 || (path.IsWindows && separator == 2 && candidate[1] == ':');
            var parentLength = keepsSeparator ? separator + 1 : separator;
            if (parentLength == candidate.Length)
            {
                return false;
            }

            candidate = candidate[..parentLength];
        }
    }
}
