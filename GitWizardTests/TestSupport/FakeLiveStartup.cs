using GitWizard;
using GitWizard.Watch;

namespace GitWizardTests.TestSupport;

/// <summary>
/// Builds the <see cref="WatchStartupResult"/> a <see cref="RepositoryDiscoveryCoordinator"/>
/// watch preparation would have produced around a test-owned source, so MainViewModel Live
/// tests can drive the production startup seam (<c>MainViewModel.LivePrepareWatchAsync</c>)
/// without a real elevated broker. The source is armed first - mirroring the coordinator's
/// arm-before-reconcile order - and disposed when the arm fails, as the coordinator's own
/// cleanup does. The inventory claims the request's retained roots, the effective membership
/// the coordinator would report when every retained root is still present.
/// </summary>
internal static class FakeLiveStartup
{
    public static async Task<WatchStartupResult> FromSourceArmedAsync(
        IVolumeChangeSource source,
        DiscoveryRequest request,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? inventoryPaths = null,
        IReadOnlyList<string>? watchVolumes = null,
        IReadOnlyList<VolumeChangeBatch>? startupBatches = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        var paths = inventoryPaths ?? request.RetainedRoots;
        var searchRoots = request.Configuration.SearchPaths
            .Select(GitWizardApi.ExpandSearchPath)
            .OfType<string>()
            .ToArray();
        var volumes = watchVolumes ?? paths.Concat(searchRoots)
            .Select(root => WatchPath.Parse(root).Volume)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        VolumeArmResult armResult;
        try
        {
            armResult = await source.ArmAndCatchUpAsync(volumes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var scope = DiscoveryScope.CreateExpanded(
            request,
            searchRoots,
            request.RetainedRoots,
            GitWizardApi.ExpandIgnoredPaths(request.Configuration.IgnoredPaths),
            root => new RootClassification(root, RootFileSystem.Ntfs, WatchPath.Parse(root).Volume, null));

        var inventory = new DiscoveryInventory
        {
            Outcome = paths.Count > 0 ? DiscoveryOutcome.CompleteNonempty : DiscoveryOutcome.CompleteEmpty,
            Route = DiscoveryRoute.Indexed,
            Paths = paths,
            RequestedNtfsDrives = volumes,
            Observations = [],
            DriveStatuses = [],
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };

        return new WatchStartupResult(inventory, armResult, source, volumes, scope, startupBatches);
    }

    /// <summary>
    /// Builds the null-source startup a coordinator produces for an unusable discovery: the
    /// controller must treat it as terminal (<c>StartupUnavailable</c>), never as a respawn.
    /// </summary>
    public static WatchStartupResult Unusable(DiscoveryRequest request, string drive, string error)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = DiscoveryScope.CreateExpanded(
            request,
            [],
            request.RetainedRoots,
            [],
            root => new RootClassification(root, RootFileSystem.Ntfs, WatchPath.Parse(root).Volume, null));

        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.Unusable,
            Route = DiscoveryRoute.Indexed,
            Paths = [],
            RequestedNtfsDrives = [],
            Observations = [],
            DriveStatuses = [],
            Errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [drive] = error },
            WatchExcludedRoots = []
        };

        return new WatchStartupResult(
            inventory, new VolumeArmResult(new Dictionary<string, string>(), []), null, [], scope);
    }
}
