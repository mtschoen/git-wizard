using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib;

namespace GitWizard;

public static partial class GitWizardApi
{
    /// <summary>
    /// Try to find all git repositories across all search paths using MFT, taking the cold
    /// scan through MFTLib's elevated journal broker when not already elevated - one UAC
    /// prompt for the whole run, no temp-file handoff.
    /// </summary>
    public static async Task<bool> TryFindAllRepositoriesUsingMftAsync(
        GitWizardConfiguration configuration, ICollection<string> paths,
        IUpdateHandler? updateHandler = null, bool noMft = false, IElevationProvider? elevation = null,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<ScanRecord>>>? scanProvider = null)
    {
        Func<IReadOnlyList<string>, Task<(JournalBrokerScanSession? Session, IReadOnlyList<ScanRecord> Records)>>?
            sessionScanProvider = null;
        if (scanProvider != null)
        {
            sessionScanProvider = async drives =>
            {
                var records = await scanProvider(drives).ConfigureAwait(false);
                return (null, records);
            };
        }

        await using var result = await TryDiscoverRepositoriesUsingMftAsync(
            configuration, paths, updateHandler, noMft, elevation, sessionScanProvider).ConfigureAwait(false);
        return result.Success;
    }

    /// <summary>
    /// Rich discovery result carrying discovered repository state along with ownership of any
    /// elevated <see cref="JournalBrokerScanSession"/> created during discovery.
    /// </summary>
    public static async Task<MftDiscoveryResult> TryDiscoverRepositoriesUsingMftAsync(
        GitWizardConfiguration configuration, ICollection<string> paths,
        IUpdateHandler? updateHandler = null, bool noMft = false, IElevationProvider? elevation = null,
        Func<IReadOnlyList<string>, Task<(JournalBrokerScanSession? Session, IReadOnlyList<ScanRecord> Records)>>?
            sessionScanProvider = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || noMft)
            return new MftDiscoveryResult(false);

        elevation ??= ElevationUtilities.DefaultProvider;
        if (elevation.IsElevated())
            return DiscoverRepositoriesInProcess(configuration, paths, updateHandler);

        return await DiscoverRepositoriesThroughBrokerAsync(
            configuration, paths, updateHandler, sessionScanProvider).ConfigureAwait(false);
    }

    static MftDiscoveryResult DiscoverRepositoriesInProcess(
        GitWizardConfiguration configuration, ICollection<string> paths, IUpdateHandler? updateHandler)
    {
        foreach (var searchPath in configuration.SearchPaths)
        {
            var expanded = ExpandSearchPath(searchPath);
            if (expanded == null)
                continue;

            FindGitRepositoriesUsingMft(expanded, paths, configuration.IgnoredPaths, updateHandler,
                configuration.SkipHiddenDirectories);
        }

        return new MftDiscoveryResult(paths.Count > 0);
    }

    [SupportedOSPlatform("windows")]
    static async Task<MftDiscoveryResult> DiscoverRepositoriesThroughBrokerAsync(
        GitWizardConfiguration configuration, ICollection<string> paths, IUpdateHandler? updateHandler,
        Func<IReadOnlyList<string>, Task<(JournalBrokerScanSession? Session, IReadOnlyList<ScanRecord> Records)>>?
            sessionScanProvider)
    {
        var expandedRoots = configuration.SearchPaths
            .Select(ExpandSearchPath)
            .OfType<string>()
            .ToList();
        var drives = expandedRoots
            .Select(GetDriveLetter)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (expandedRoots.Count == 0 || drives.Count == 0)
            return new MftDiscoveryResult(false);

        (JournalBrokerScanSession? Session, IReadOnlyList<ScanRecord> Records) scanResult;
        try
        {
            if (sessionScanProvider != null)
            {
                scanResult = await sessionScanProvider(drives).ConfigureAwait(false);
            }
            else
            {
                var brokerResult = await BrokerScanWithSessionAsync(drives).ConfigureAwait(false);
                scanResult = (brokerResult.Session, brokerResult.Records);
            }
        }
        catch (InvalidOperationException exception)
        {
            GitWizardLog.Log($"MFT broker scan unavailable: {exception.Message}", GitWizardLog.LogType.Warning);
            return new MftDiscoveryResult(false);
        }

        return await BuildDiscoveryResultAsync(
            configuration, paths, updateHandler, expandedRoots, drives, scanResult).ConfigureAwait(false);
    }

    static async Task<MftDiscoveryResult> BuildDiscoveryResultAsync(
        GitWizardConfiguration configuration, ICollection<string> paths, IUpdateHandler? updateHandler,
        IReadOnlyList<string> expandedRoots, IReadOnlyList<string> drives,
        (JournalBrokerScanSession? Session, IReadOnlyList<ScanRecord> Records) scanResult)
    {
        try
        {
            foreach (var searchPath in expandedRoots)
            {
                CollectGitRepositoriesFromScan(scanResult.Records, searchPath, configuration.IgnoredPaths, paths,
                    configuration.SkipHiddenDirectories);
            }

            updateHandler?.SendUpdateMessage($"MFT search found {paths.Count} repositories");
            if (paths.Count > 0)
                return new MftDiscoveryResult(true, scanResult.Session, scanResult.Records, drives);

            if (scanResult.Session != null)
                await scanResult.Session.DisposeAsync().ConfigureAwait(false);

            return new MftDiscoveryResult(false);
        }
        catch
        {
            if (scanResult.Session != null)
                await scanResult.Session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    static async Task<(JournalBrokerScanSession Session, IReadOnlyList<ScanRecord> Records)>
        BrokerScanWithSessionAsync(IReadOnlyList<string> drives)
    {
        var records = new List<ScanRecord>();
        var session = await JournalBrokerScanSession
            .StartAsync(BrokerLauncher.Launch, drives, GitDiscoveryScanProfile,
                (batch, _) =>
                {
                    records.AddRange(batch);
                    return ValueTask.CompletedTask;
                },
                keepFileNames: [GitEntryName], cancellationToken: CancellationToken.None).ConfigureAwait(false);

        return (session, records);
    }
}
