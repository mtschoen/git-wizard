using MFTLib;

namespace GitWizardUI.ViewModels;

/// <summary>
/// Encapsulates a reusable discovery session and the scan records that seed its path index.
/// </summary>
public sealed class PreparedDiscoverySession : IAsyncDisposable
{
    public JournalBrokerScanSession Session { get; }
    public IReadOnlyList<ScanRecord> ScanRecords { get; }
    public IReadOnlyList<string>? Drives { get; }

    public PreparedDiscoverySession(
        JournalBrokerScanSession session,
        IReadOnlyList<ScanRecord> scanRecords,
        IReadOnlyList<string>? drives = null)
    {
        Session = session;
        ScanRecords = scanRecords;
        Drives = drives;
    }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync().ConfigureAwait(false);
    }
}
