using MFTLib;

namespace GitWizard;

/// <summary>
/// Result of an MFT-backed repository discovery pass, carrying discovered repository state along
/// with optional ownership of the elevated <see cref="JournalBrokerScanSession"/> used for the scan.
/// </summary>
public sealed class MftDiscoveryResult : IAsyncDisposable
{
    public bool Success { get; }
    public JournalBrokerScanSession? Session { get; private set; }
    public IReadOnlyList<ScanRecord>? ScanRecords { get; }
    public IReadOnlyList<string>? Drives { get; }

    public MftDiscoveryResult(
        bool success,
        JournalBrokerScanSession? session = null,
        IReadOnlyList<ScanRecord>? scanRecords = null,
        IReadOnlyList<string>? drives = null)
    {
        Success = success;
        Session = session;
        ScanRecords = scanRecords;
        Drives = drives;
    }

    /// <summary>
    /// Transfers ownership of the session to the caller. The result will no longer dispose it.
    /// </summary>
    public JournalBrokerScanSession? DetachSession()
    {
        var session = Session;
        Session = null;
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        if (Session is not null)
        {
            await Session.DisposeAsync().ConfigureAwait(false);
            Session = null;
        }
    }
}
