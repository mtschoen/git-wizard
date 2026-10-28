using System.Runtime.Versioning;
using MFTLib;
using MFTLib.Index;

namespace GitWizard;

public sealed partial class MftIndexSession
{
    [SupportedOSPlatform("windows")]
    public static Task<MftIndexSession> OpenWindowsAsync(IReadOnlyList<string> drives,
        Func<string, bool> brokerLauncher, TimeSpan? connectTimeout, CancellationToken cancellationToken)
    {
        return OpenWindowsAsync(drives, brokerLauncher, connectTimeout, null, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    public static Task<MftIndexSession> OpenWindowsAsync(IReadOnlyList<string> drives,
        Func<string, bool> brokerLauncher, TimeSpan? connectTimeout,
        IProgress<IndexScanProgress>? progress, CancellationToken cancellationToken)
    {
        return OpenWindowsAsync(drives, brokerLauncher, CacheMode.Persistent, connectTimeout,
            progress, cancellationToken);
    }

    /// <summary>
    /// Opens the selected volumes over git-wizard's private cache. <see cref="CacheMode.ExistingOnly" />
    /// adopts cached blocks only and reports every other drive as a typed refusal without launching the broker.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Task<MftIndexSession> OpenWindowsAsync(IReadOnlyList<string> drives,
        Func<string, bool> brokerLauncher, CacheMode cacheMode, TimeSpan? connectTimeout,
        IProgress<IndexScanProgress>? progress, CancellationToken cancellationToken)
    {
        return OpenSelectedVolumesAsync(drives,
            new BrokerSession(brokerLauncher, connectTimeout),
            new CacheSelection(MftIndexCachePolicy.GetCacheDirectory(GitWizardApi.GetLocalFilesPath()), cacheMode),
            progress, cancellationToken);
    }

    internal static Task<MftIndexSession> OpenSelectedVolumesAsync(IReadOnlyList<string> drives,
        BrokerSession brokerSession,
        string cacheDirectory, IProgress<IndexScanProgress>? progress, CancellationToken cancellationToken,
        Func<FileIndexOptions, FileIndexOptions>? configureOptions = null) =>
        OpenSelectedVolumesAsync(drives, brokerSession,
            new CacheSelection(cacheDirectory, CacheMode.Persistent), progress, cancellationToken, configureOptions);

    internal readonly record struct CacheSelection(string Directory, CacheMode CacheMode);

    internal static async Task<MftIndexSession> OpenSelectedVolumesAsync(IReadOnlyList<string> drives,
        BrokerSession brokerSession,
        CacheSelection cache, IProgress<IndexScanProgress>? progress, CancellationToken cancellationToken,
        Func<FileIndexOptions, FileIndexOptions>? configureOptions = null)
    {
        var requested = RootKeyProjection.ParseRootKeys(drives);
        var session = await OpenAsync(requested, brokerSession, cache.Directory, cache.CacheMode, progress,
            cancellationToken, cache.CacheMode == CacheMode.Disabled
                ? ProducerSelection.WalkOnly : ProducerSelection.Automatic, configureOptions).ConfigureAwait(false);
        return session;
    }
}
