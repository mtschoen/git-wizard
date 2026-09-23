using System.Buffers;
using MFTLib.Index;

namespace GitWizard;

public static class MftIndexCacheMaintenance
{
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
        foreach (var directory in GetPolicyDirectories(localFilesPath, errors))
        {
            try
            {
                if (Directory.Exists(directory) &&
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add($"Skipped linked policy directory: {directory}");
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

    static string[] GetPolicyDirectories(string localFilesPath, List<string> errors)
    {
        var root = MftIndexCachePolicy.GetCacheRoot(localFilesPath);
        var directories = new HashSet<string>(StringComparer.Ordinal)
        {
            MftIndexCachePolicy.Current.GetCacheDirectory(localFilesPath)
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
                    if (name.Length == 67 && name.StartsWith("v1-", StringComparison.Ordinal) &&
                        name.AsSpan(3).IndexOfAnyExcept(HexDigits) < 0)
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
