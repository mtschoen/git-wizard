using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MFTLib;

namespace GitWizard;

/// <summary>
/// Scan identity of git-wizard's private MFT index and the cache directory derived from it.
/// Any change to the profile or retention list selects a different directory, so a block
/// produced under one policy is never adopted under another.
/// </summary>
public sealed class MftIndexCachePolicy
{
    public const BrokerScanProfile DiscoveryProfile = BrokerScanProfile.DirectoryIndex;
    const string CacheRootName = "mft-index";
    const string IdentityVersion = "v1";

    public static MftIndexCachePolicy Current { get; } = new(DiscoveryProfile, [".git", "HEAD"]);

    public BrokerScanProfile Profile { get; }
    public IReadOnlyList<string> KeepFileNames { get; }
    readonly string _directoryName;

    internal MftIndexCachePolicy(BrokerScanProfile profile, IEnumerable<string> keepFileNames)
    {
        ArgumentNullException.ThrowIfNull(keepFileNames);
        var names = keepFileNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var name in names)
        {
            ArgumentException.ThrowIfNullOrEmpty(name, nameof(keepFileNames));
            if (name.IndexOfAny(['/', '\\', '\0']) >= 0)
                throw new ArgumentException("Retention entries must be file names.", nameof(keepFileNames));
        }
        Profile = profile;
        KeepFileNames = Array.AsReadOnly(names);
        var identity = new StringBuilder("git-wizard-mft-index-").Append(IdentityVersion).Append('\n');
        identity.Append(((int)profile).ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var name in names)
            identity.Append(name.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(name).Append('\n');
        _directoryName = IdentityVersion + "-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
    }

    public static string GetCacheRoot(string localFilesPath) => Path.Combine(localFilesPath, CacheRootName);

    public string GetCacheDirectory(string localFilesPath) =>
        Path.Combine(GetCacheRoot(localFilesPath), _directoryName);
}
