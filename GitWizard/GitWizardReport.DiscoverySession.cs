using System.Text.Json.Serialization;
using MFTLib;

namespace GitWizard;

public partial class GitWizardReport
{
    /// <summary>
    /// Transient elevated scan session retained from a cold MFT discovery pass when requested.
    /// Never serialized into report.json.
    /// </summary>
    [JsonIgnore]
    public JournalBrokerScanSession? DiscoverySession { get; private set; }

    /// <summary>
    /// Scan records from the cold discovery scan associated with <see cref="DiscoverySession"/>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ScanRecord>? DiscoveryScanRecords { get; private set; }

    /// <summary>
    /// Drive letters scanned by <see cref="DiscoverySession"/>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string>? DiscoveryDrives { get; private set; }

    /// <summary>
    /// Transfers ownership of the retained discovery session, records, and drives to the caller.
    /// </summary>
    public (JournalBrokerScanSession? Session, IReadOnlyList<ScanRecord>? ScanRecords, IReadOnlyList<string>? Drives)
        DetachDiscoverySession()
    {
        var session = DiscoverySession;
        var records = DiscoveryScanRecords;
        var drives = DiscoveryDrives;
        DiscoverySession = null;
        DiscoveryScanRecords = null;
        DiscoveryDrives = null;
        return (session, records, drives);
    }

    public async ValueTask DisposeAsync()
    {
        if (DiscoverySession is not null)
        {
            await DiscoverySession.DisposeAsync().ConfigureAwait(false);
            DiscoverySession = null;
            DiscoveryScanRecords = null;
            DiscoveryDrives = null;
        }

        GC.SuppressFinalize(this);
    }

    public async Task GetRepositoryPathsAsync(ICollection<string> repositoryPaths,
        IUpdateHandler? updateHandler = null, bool noMft = false, bool retainDiscoverySession = false)
    {
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = SearchPaths,
            IgnoredPaths = IgnoredPaths,
            SkipHiddenDirectories = SkipHiddenDirectories
        };

        if (retainDiscoverySession)
        {
            var discoveryResult = await GitWizardApi.TryDiscoverRepositoriesUsingMftAsync(
                configuration, repositoryPaths, updateHandler, noMft).ConfigureAwait(false);
            if (discoveryResult.Success)
            {
                DiscoverySession = discoveryResult.DetachSession();
                DiscoveryScanRecords = discoveryResult.ScanRecords;
                DiscoveryDrives = discoveryResult.Drives;
                return;
            }
        }
        else if (await GitWizardApi.TryFindAllRepositoriesUsingMftAsync(
                     configuration, repositoryPaths, updateHandler, noMft).ConfigureAwait(false))
        {
            return;
        }

        var count = 0;
        try
        {
            updateHandler?.StartProgress("Getting GitWizardRepository Paths", SearchPaths.Count);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception thrown by GetRepositoryPaths StartProgress callback.");
        }

        Parallel.ForEach(SearchPaths, path =>
        {
            GitWizardApi.GetRepositoryPaths(path, repositoryPaths, IgnoredPaths, updateHandler);

            try
            {
                updateHandler?.UpdateProgress(++count);
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, "Exception thrown by GetRepositoryPaths UpdateProgress callback.");
            }
        });
    }
}
