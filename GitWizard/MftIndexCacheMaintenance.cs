using System.Buffers;
using MFTLib.Index;

namespace GitWizard;

public static class MftIndexCacheMaintenance
{
    // Earlier releases named the cache directory "v1-<64 hex SHA-256 digits>" after the scan shape.
    // Nothing writes such a directory now; Run still lists them so Clear reclaims their blocks.
    const string LegacyDirectoryPrefix = "v1-";
    const int LegacyDirectoryNameLength = 67;
    static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789ABCDEF");

    public static MftIndexCacheMaintenanceResult Inspect(string localFilesPath) =>
        Run(localFilesPath, clear: false);

    public static MftIndexCacheMaintenanceResult Clear(string localFilesPath) =>
        Run(localFilesPath, clear: true);

    static MftIndexCacheMaintenanceResult Run(string localFilesPath, bool clear)
    {
        ArgumentException.ThrowIfNullOrEmpty(localFilesPath);
        var errors = new List<string>();
        var deletions = new List<CachedBlockDeletionResult>();
        var inventory = new List<CachedBlockStatus>();
        foreach (var directory in GetCacheDirectories(localFilesPath, errors))
        {
            try
            {
                if (Directory.Exists(directory) &&
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add($"Skipped linked cache directory: {directory}");
                    continue;
                }

                if (clear)
                {
                    deletions.AddRange(CacheDirectory.DeleteCached(directory));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{directory}: {exception.Message}");
            }

            try
            {
                inventory.AddRange(CacheDirectory.InspectCached(directory));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Inspection of {directory}: {exception.Message}");
            }
        }

        return new MftIndexCacheMaintenanceResult(
            Array.AsReadOnly(deletions.OrderBy(row => row.File.Path, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(inventory.OrderBy(row => row.File.Path, StringComparer.Ordinal).ToArray()),
            errors.AsReadOnly());
    }

    static string[] GetCacheDirectories(string localFilesPath, List<string> errors)
    {
        var root = MftIndexCachePolicy.GetCacheRoot(localFilesPath);
        var directories = new HashSet<string>(StringComparer.Ordinal)
        {
            MftIndexCachePolicy.GetCacheDirectory(localFilesPath)
        };

        try
        {
            if (Directory.Exists(root))
            {
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add($"Skipped linked cache root: {root}");
                    return [];
                }

                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(directory);
                    if (name.Length == LegacyDirectoryNameLength &&
                        name.StartsWith(LegacyDirectoryPrefix, StringComparison.Ordinal) &&
                        name.AsSpan(LegacyDirectoryPrefix.Length).IndexOfAnyExcept(HexDigits) < 0)
                    {
                        directories.Add(directory);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Listing {root}: {exception.Message}");
        }

        return directories.Order(StringComparer.Ordinal).ToArray();
    }
}
