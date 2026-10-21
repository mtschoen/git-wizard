using System.Diagnostics.CodeAnalysis;

namespace GitWizard;

public static partial class GitWizardApi
{
    /// <summary>
    /// Deletes every local file unless the persistent index cache subtree exists, in which case
    /// nothing is touched and <paramref name="refusal" /> says why. This is not index-cache clearing:
    /// the cache belongs to MFTLib's lock-safe ownership, so this operation never enters it.
    /// </summary>
    /// <returns>True when the local files are absent or were deleted; false with a reason otherwise.</returns>
    public static bool TryDeleteAllLocalFiles([NotNullWhen(false)] out string? refusal)
    {
        refusal = null;
        var root = GetLocalFilesPath();
        var reserved = MftIndexCachePolicy.GetCacheRoot(root);
        try
        {
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(root);
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }

            if (entries.Any(entry => string.Equals(entry, reserved, StringComparison.OrdinalIgnoreCase)))
            {
                refusal = "Cannot delete all local files while mft-index exists. " +
                    "Persistent index-cache clearing is not supported by this command.";
                return false;
            }

            GitWizardLog.CloseCurrentLogFile();
            foreach (var entry in entries)
            {
                DeleteEntry(entry);
            }
            // Nonrecursive, so a cache created after the snapshot above fails the delete instead of being entered.
            Directory.Delete(root, recursive: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            refusal = $"Could not delete all local files: {exception.Message}";
            return false;
        }
    }

    static void DeleteEntry(string entry)
    {
        var attributes = File.GetAttributes(entry);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.Delete(entry, recursive: (attributes & FileAttributes.ReparsePoint) == 0);
        }
        else
        {
            File.Delete(entry);
        }
    }
}
