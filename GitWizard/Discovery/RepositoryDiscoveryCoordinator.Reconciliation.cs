using GitWizard.Watch;

namespace GitWizard.Discovery;

public partial class RepositoryDiscoveryCoordinator(RepositoryDiscoveryDependencies dependencies)
{
    readonly RepositoryDiscoveryDependencies? _dependencies = dependencies;

    public RepositoryDiscoveryCoordinator()
        : this(RepositoryDiscoveryDependencies.CreateDefault())
    {
    }

    static bool Contains(RootObservation observation, string path)
    {
        var target = WatchPath.Parse(path);
        var root = WatchPath.Parse(observation.Root);
        return observation.ExactRoot ? target.IsEqualTo(root) : target.IsUnderOrEqual(root);
    }

    /// <summary>
    /// Reconciles a previous repository list with a discovery inventory: an unusable
    /// inventory keeps the previous list, a publishable full inventory replaces it, and a
    /// partial inventory retains previous entries the inventory did not completely observe.
    /// </summary>
    public static IReadOnlyList<string> ReconcilePaths(
        IReadOnlyList<string> previous, DiscoveryInventory inventory)
    {
        if (inventory.Outcome == DiscoveryOutcome.Unusable)
        {
            return (string[])[.. previous];
        }

        if (inventory.CanPublishFullInventory &&
            inventory.Observations.All(item => item.Source != DiscoverySource.Snapshot))
        {
            return (string[])[.. inventory.Paths];
        }

        var retained = previous.Where(path =>
        {
            var incompleteSpecificity = inventory.Observations
                .Where(item => !item.Complete && Contains(item, path))
                .Select(item => WatchPath.Parse(item.Root).Comparison.Length)
                .DefaultIfEmpty(-1).Max();
            var completeSpecificity = inventory.Observations
                .Where(item => item.Complete && item.Source != DiscoverySource.Snapshot && Contains(item, path))
                .Select(item => WatchPath.Parse(item.Root).Comparison.Length)
                .DefaultIfEmpty(-1).Max();
            return incompleteSpecificity >= 0
                ? incompleteSpecificity >= completeSpecificity
                : completeSpecificity < 0;
        });

        return (string[])[.. retained.Concat(inventory.Paths).Distinct(WatchPath.StringComparer)];
    }

    async Task PersistAsync(
        IReadOnlyList<string> previous, DiscoveryInventory inventory, CancellationToken cancellationToken)
    {
        if (inventory.Outcome == DiscoveryOutcome.Unusable)
        {
            return;
        }

        if (_dependencies is null)
        {
            return;
        }

        var pathsToSave = inventory.CanPublishFullInventory &&
            inventory.Observations.All(item => item.Source != DiscoverySource.Snapshot)
            ? inventory.Paths
            : ReconcilePaths(previous, inventory);

        await _dependencies.WriteListAsync(pathsToSave, cancellationToken).ConfigureAwait(false);
    }
}
