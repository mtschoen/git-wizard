using MFTLib.Index;

namespace GitWizard;

/// <summary>
/// Scan shape of git-wizard's private MFT index and the one cache directory that holds its blocks.
/// MFTLib stamps <see cref="CacheTag" /> into every block and discards a block whose tag differs, so
/// a changed scan shape cold-scans instead of adopting an older block.
/// </summary>
public static class MftIndexCachePolicy
{
    const string CacheRootName = "mft-index";
    const string BlockDirectoryName = "blocks";

    // MUST be incremented whenever DirectoryScanFileNames changes: the tag version is the
    // only thing that invalidates blocks cached under the previous shape. MftIndexCachePolicyTests pins
    // ScanShape per version and fails until a new version row is added.
    const uint CacheTagVersion = 2;

    public static IReadOnlyList<string> DirectoryScanFileNames { get; } = Array.AsReadOnly([".git", "HEAD"]);

    public static CacheTag CacheTag { get; } = new("GWMI", CacheTagVersion);

    /// <summary>Directory scan retention identity, pinned per version by the tests.</summary>
    internal static string ScanShape => $"directories|{string.Join(',', DirectoryScanFileNames)}";

    public static string GetCacheRoot(string localFilesPath) => Path.Combine(localFilesPath, CacheRootName);

    public static string GetCacheDirectory(string localFilesPath) =>
        Path.Combine(GetCacheRoot(localFilesPath), BlockDirectoryName);
}
