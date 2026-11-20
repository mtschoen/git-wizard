namespace GitWizard.Watch;

/// <summary>
/// Compares the keys of scan-error maps. A key is a Windows drive ("X") or a Linux watch root
/// ("/srv/Repos"): POSIX paths are case-sensitive, so a key beginning with a slash compares
/// ordinally, while every other key (a bare drive letter or Windows-rooted path) folds case.
/// </summary>
sealed class ScanErrorKeyComparer : IEqualityComparer<string>
{
    public static ScanErrorKeyComparer Instance { get; } = new();

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

        return IsPosix(x) || IsPosix(y)
            ? string.Equals(x, y, StringComparison.Ordinal)
            : string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(string obj) =>
        IsPosix(obj)
            ? StringComparer.Ordinal.GetHashCode(obj)
            : StringComparer.OrdinalIgnoreCase.GetHashCode(obj);

    static bool IsPosix(string key) => key.StartsWith('/');
}
