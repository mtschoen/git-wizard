using MFTLib.Index;

namespace GitWizard;

/// <summary>
/// Application cache identity and the directory holding GitWizard's index blocks.
/// </summary>
public static class MftIndexCachePolicy
{
    const string CacheRootName = "mft-index";
    const string BlockDirectoryName = "blocks";

    // The tag identifies application semantics; MFTLib validates row-shaping options independently.
    const uint CacheTagVersion = 3;

    public static CacheTag CacheTag { get; } = new("GWMI", CacheTagVersion);

    public static string GetCacheRoot(string localFilesPath) => Path.Combine(localFilesPath, CacheRootName);

    public static string GetCacheDirectory(string localFilesPath) =>
        Path.Combine(GetCacheRoot(localFilesPath), BlockDirectoryName);
}
