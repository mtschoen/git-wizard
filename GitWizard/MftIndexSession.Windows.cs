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
        return OpenWindowsAsync(drives, brokerLauncher, null, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    public static Task<MftIndexSession> OpenWindowsAsync(IReadOnlyList<string> drives,
        Func<string, bool> brokerLauncher, TimeSpan? connectTimeout, CancellationToken cancellationToken)
    {
        return OpenSelectedVolumesAsync(drives, IndexedDrive.FromWindowsVolume,
            token => connectTimeout.HasValue
                ? JournalBrokerClient.SpawnAndConnectAsync(brokerLauncher, connectTimeout.Value, token)
                : JournalBrokerClient.SpawnAndConnectAsync(brokerLauncher, token),
            Path.Combine(GitWizardApi.GetLocalFilesPath(), "mft-index"), cancellationToken);
    }

    internal static async Task<MftIndexSession> OpenSelectedVolumesAsync(IReadOnlyList<string> drives,
        Func<string, IndexedDrive> acquireIdentity,
        Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
        string cacheDirectory, CancellationToken cancellationToken)
    {
        var requested = drives.Select(NormalizeVolume).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var identities = new List<IndexedDrive>();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                identities.Add(acquireIdentity(drive));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors[drive] = exception.Message;
            }
        }

        var session = await OpenAsync(identities, connectAsync, cacheDirectory, cancellationToken).ConfigureAwait(false);
        try
        {
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
