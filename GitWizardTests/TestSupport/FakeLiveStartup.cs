using GitWizard;
using GitWizard.Watch;

namespace GitWizardTests.TestSupport;

/// <summary>
/// Builds the <see cref="WatchStartupResult"/> a <see cref="RepositoryDiscoveryCoordinator"/>
/// watch preparation would have produced around a test-owned source, so MainViewModel Live
/// tests can drive the production startup seam (<c>LiveWatchViewModel.PrepareWatchAsync</c>)
/// without a real elevated broker. The source is armed first - mirroring the coordinator's
/// arm-before-reconcile order - and disposed when the arm fails, as the coordinator's own
/// cleanup does. The inventory claims the request's retained roots, the effective membership
/// the coordinator would report when every retained root is still present.
/// </summary>
static class FakeLiveStartup
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
        var searchRoots = request.Configuration.SearchPaths.ToArray();
        var volumes = watchVolumes ?? (string[])[.. paths.Concat(searchRoots)
            .Select(root => WatchPath.Parse(root).Volume)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];

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
            (string[])[.. request.Configuration.IgnoredPaths]);

        var inventory = new DiscoveryInventory
        {
            Outcome = paths.Count > 0 ? DiscoveryOutcome.CompleteNonempty : DiscoveryOutcome.CompleteEmpty,
            Route = DiscoveryRoute.Indexed,
            Paths = paths,
            RequestedNtfsDrives = volumes,
            Observations = [],
            RootStatuses = [],
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };

        return new WatchStartupResult(inventory, armResult, source, volumes, scope, startupBatches);
    }

    /// <summary>
    /// Wraps a fake source factory in the startup factory shape the production controller takes:
    /// each start or respawn calls <paramref name="sourceFactory"/> once and arms the result the
    /// way the coordinator would, over the given tracked and search roots.
    /// </summary>
    public static Func<IProgress<WatchStartupProgress>, CancellationToken, Task<WatchStartupResult>> Factory(
        Func<IVolumeChangeSource> sourceFactory,
        IReadOnlyList<string> trackedRoots,
        IReadOnlyList<string> searchRoots,
        IReadOnlyList<string>? ignoredPaths = null,
        bool includeTempDirectories = false)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        var configuration = new GitWizardConfiguration { IncludeTempDirectories = includeTempDirectories };
        foreach (var searchRoot in searchRoots)
        {
            configuration.SearchPaths.Add(searchRoot);
        }

        foreach (var ignoredPath in ignoredPaths ?? [])
        {
            configuration.IgnoredPaths.Add(ignoredPath);
        }

        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false, (string[])[.. trackedRoots]);
        return (_, cancellationToken) => FromSourceArmedAsync(sourceFactory(), request, cancellationToken);
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
            []);

        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.Unusable,
            Route = DiscoveryRoute.Indexed,
            Paths = [],
            RequestedNtfsDrives = [],
            Observations = [],
            RootStatuses = [],
            Errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [drive] = error },
            WatchExcludedRoots = []
        };

        return new WatchStartupResult(
            inventory, new VolumeArmResult(new Dictionary<string, string>(), []), null, [], scope);
    }
}
