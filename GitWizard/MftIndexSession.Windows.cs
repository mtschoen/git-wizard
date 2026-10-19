using System.Runtime.Versioning;
using MFTLib;
using MFTLib.Index;

namespace GitWizard;

public sealed partial class MftIndexSession
{
    [SupportedOSPlatform("windows")]
    public static Task<MftIndexSession> OpenWindowsAsync(IReadOnlyList<string> drives,
        Func<string, bool> brokerLauncher, CancellationToken cancellationToken)
    {
        return OpenWindowsAsync(drives, brokerLauncher, null, null, cancellationToken);
    }

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
        return OpenSelectedVolumesAsync(drives, IndexedDrive.FromWindowsVolume,
            new BrokerSession(brokerLauncher, connectTimeout),
            new CacheSelection(MftIndexCachePolicy.GetCacheDirectory(GitWizardApi.GetLocalFilesPath()), cacheMode),
            progress, cancellationToken);
    }

    internal static Task<MftIndexSession> OpenSelectedVolumesAsync(IReadOnlyList<string> drives,
        Func<DriveKey, IndexedDrive> acquireIdentity,
        BrokerSession brokerSession,
        string cacheDirectory, IProgress<IndexScanProgress>? progress, CancellationToken cancellationToken) =>
        OpenSelectedVolumesAsync(drives, acquireIdentity, brokerSession,
            new CacheSelection(cacheDirectory, CacheMode.Persistent), progress, cancellationToken);

    internal readonly record struct CacheSelection(string Directory, CacheMode CacheMode);

    internal static async Task<MftIndexSession> OpenSelectedVolumesAsync(IReadOnlyList<string> drives,
        Func<DriveKey, IndexedDrive> acquireIdentity,
        BrokerSession brokerSession,
        CacheSelection cache, IProgress<IndexScanProgress>? progress, CancellationToken cancellationToken)
    {
        var requested = drives.Select(NormalizeVolume).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var identities = new List<IndexedDrive>();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var drive in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    identities.Add(acquireIdentity(drive[0]));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    errors[drive] = exception.Message;
                }
            }
        }
        catch
        {
            // OpenAsync below owns the session only once it is called; an earlier escape must not leak it.
            await brokerSession.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var session = await OpenAsync(identities, brokerSession, cache.Directory, cache.CacheMode, progress,
            cancellationToken).ConfigureAwait(false);
        try
        {
            session._identityFailures = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(errors, StringComparer.OrdinalIgnoreCase));
            foreach (var error in session.ScanErrors)
                errors[error.Key] = error.Value;
            session.Drives = Array.AsReadOnly(requested);
            session.ScanErrors = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(errors);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    static string? NormalizeVolume(string drive)
    {
        var token = drive.AsSpan().Trim();
        if (token.Length == 0 || !char.IsAsciiLetter(token[0]))
            return null;
        if (token.Length != 1 && !(token.Length == 2 && token[1] == ':') &&
            !(token.Length == 3 && token[1] == ':' && token[2] is '\\' or '/'))
            return null;
        return char.ToUpperInvariant(token[0]).ToString();
    }
}
