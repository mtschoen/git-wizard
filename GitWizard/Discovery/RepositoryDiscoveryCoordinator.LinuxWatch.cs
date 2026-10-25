using GitWizard.Watch;

namespace GitWizard.Discovery;

public partial class RepositoryDiscoveryCoordinator
{
    async Task<WatchStartupResult> PrepareLinuxWatchAsync(
        DiscoveryRequest request,
        DiscoveryScope scope,
        IProgress<WatchStartupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var dependencies = Dependencies;
        var previousList = await dependencies.ReadListAsync(cancellationToken).ConfigureAwait(false) ?? [];
        var source = dependencies.CreateLinuxSource();
        var accumulator = new DiscoveryAccumulator(scope, new HashSet<string>(WatchPath.StringComparer));

        try
        {
            progress?.Report(new WatchStartupProgress(WatchStartupPhase.Opening));
            var allRoots = scope.SearchRoots.Concat(scope.RetainedRoots)
                .Distinct(WatchPath.StringComparer)
                .ToArray();

            var armResult = await source.ArmAndCatchUpAsync(allRoots, cancellationToken).ConfigureAwait(false);
            foreach (var (root, message) in armResult.Errors)
            {
                accumulator.Errors[root] = message;
            }

            if (allRoots.Length > 0 && armResult.Errors.Count == allRoots.Length)
            {
                await source.DisposeAsync().ConfigureAwait(false);
                source = null;
                return CreateUnmarkedLinuxStartup(armResult, accumulator);
            }

            progress?.Report(new WatchStartupProgress(WatchStartupPhase.Discovering));
            await ObserveMarkedLinuxRootsAsync(request, armResult, accumulator, cancellationToken).ConfigureAwait(false);
            var inventory = accumulator.BuildInventory(DiscoveryRoute.RecursiveOnly, []);
            await PersistAsync(previousList, inventory, cancellationToken).ConfigureAwait(false);

            if (inventory.Outcome is DiscoveryOutcome.CompleteEmpty or DiscoveryOutcome.Unusable)
            {
                await source.DisposeAsync().ConfigureAwait(false);
                source = null;
            }

            var result = new WatchStartupResult(inventory, armResult, source, [LinuxWatchRouting.Volume], scope);
            source = null;
            return result;
        }
        finally
        {
            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    static WatchStartupResult CreateUnmarkedLinuxStartup(VolumeArmResult armResult, DiscoveryAccumulator accumulator)
    {
        var scope = accumulator.Scope;
        foreach (var root in scope.SearchRoots)
        {
            accumulator.Observations.Add(new RootObservation(root, false, false, DiscoverySource.Scan, null,
                armResult.Errors.GetValueOrDefault(root, "Mark failed.")));
        }

        foreach (var root in scope.RetainedRoots)
        {
            accumulator.Observations.Add(new RootObservation(root, true, false, DiscoverySource.Scan, null,
                armResult.Errors.GetValueOrDefault(root, "Mark failed.")));
        }

        var unusableInventory = accumulator.BuildInventory(DiscoveryRoute.RecursiveOnly, []);
        return new WatchStartupResult(unusableInventory, armResult, null, [LinuxWatchRouting.Volume], scope);
    }

    async Task ObserveMarkedLinuxRootsAsync(
        DiscoveryRequest request,
        VolumeArmResult armResult,
        DiscoveryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var dependencies = Dependencies;
        var scope = accumulator.Scope;
        foreach (var root in scope.SearchRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (armResult.Errors.TryGetValue(root, out var markError))
            {
                accumulator.Observations.Add(new RootObservation(root, false, false, DiscoverySource.Scan, null, markError));
                continue;
            }

            var crawlResult = await dependencies.CrawlRootAsync(request, root, cancellationToken).ConfigureAwait(false);
            accumulator.AddCrawlResult(root, crawlResult, DiscoverySource.Scan);
        }

        foreach (var root in scope.RetainedRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (armResult.Errors.TryGetValue(root, out var markError))
            {
                accumulator.Observations.Add(new RootObservation(root, true, false, DiscoverySource.Scan, null, markError));
                continue;
            }

            var exists = dependencies.CandidateExists(root, RepositoryCandidateKind.Ordinary) ||
                         dependencies.CandidateExists(root, RepositoryCandidateKind.GitFile) ||
                         dependencies.CandidateExists(root, RepositoryCandidateKind.Bare);
            if (exists && CandidateAdmissionPolicy.AdmitsInventoryCandidate(scope, root))
            {
                accumulator.Paths.Add(root);
            }

            accumulator.Observations.Add(new RootObservation(root, true, true, DiscoverySource.Scan, null, null));
        }
    }
}
