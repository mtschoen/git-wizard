using GitWizard.Watch;

namespace GitWizard;

public partial class RepositoryDiscoveryCoordinator
{
    readonly RepositoryDiscoveryDependencies? _dependencies;

    public RepositoryDiscoveryCoordinator()
        : this(RepositoryDiscoveryDependencies.CreateDefault())
    {
    }

    public RepositoryDiscoveryCoordinator(RepositoryDiscoveryDependencies dependencies)
    {
        _dependencies = dependencies;
    }

    internal static bool Contains(RootObservation observation, string path)
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
            return previous.ToArray();

        if (inventory.CanPublishFullInventory &&
            inventory.Observations.All(item => item.Source != DiscoverySource.Snapshot))
            return inventory.Paths.ToArray();

        var retained = previous.Where(path =>
            inventory.Observations.Any(item => !item.Complete && Contains(item, path)) ||
            !inventory.Observations.Any(item => item.Complete && item.Source != DiscoverySource.Snapshot && Contains(item, path)));

        return retained.Concat(inventory.Paths).Distinct(WatchPath.StringComparer).ToArray();
    }

    internal async Task PersistAsync(
        IReadOnlyList<string> previous, DiscoveryInventory inventory, CancellationToken cancellationToken)
    {
        if (inventory.Outcome == DiscoveryOutcome.Unusable)
            return;

        if (_dependencies is null)
            return;

        var pathsToSave = inventory.CanPublishFullInventory &&
            inventory.Observations.All(item => item.Source != DiscoverySource.Snapshot)
            ? inventory.Paths
            : ReconcilePaths(previous, inventory);

        await _dependencies.WriteListAsync(pathsToSave, cancellationToken).ConfigureAwait(false);
    }
}
