using MFTLib.Index;

namespace GitWizard;

/// <summary>
/// Application cache identity, marker retention, and the directory holding GitWizard's index blocks.
/// </summary>
public static class MftIndexCachePolicy
{
    const string CacheRootName = "mft-index";
    const string BlockDirectoryName = "blocks";

    // The tag identifies application semantics; MFTLib validates row-shaping options independently.
    const uint CacheTagVersion = 2;

    public static IReadOnlyList<string> DirectoryScanFileNames { get; } = Array.AsReadOnly([".git", "HEAD"]);

    public static CacheTag CacheTag { get; } = new("GWMI", CacheTagVersion);

    public static string GetCacheRoot(string localFilesPath) => Path.Combine(localFilesPath, CacheRootName);

    public static string GetCacheDirectory(string localFilesPath) =>
        Path.Combine(GetCacheRoot(localFilesPath), BlockDirectoryName);
}
