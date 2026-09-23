namespace GitWizard.CLI;

public static partial class Program
{
    internal static int? GetDiscoveryExitCode(DiscoveryInventory? inventory, bool fullInventory)
    {
        return fullInventory && inventory is { CanPublishFullInventory: false } ? 3 : null;
    }

    internal static Task<RepositoryDiscoveryResult> DiscoverForCommandAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator? coordinator = null,
        IUpdateHandler? updateHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        coordinator ??= new RepositoryDiscoveryCoordinator();

        var purpose = runConfiguration.RebuildRepositoryList || runConfiguration.ScanOnly
            ? DiscoveryPurpose.Rebuild
            : DiscoveryPurpose.Ordinary;

        var allowList = !runConfiguration.RebuildRepositoryList && !runConfiguration.ScanOnly;

        var request = new DiscoveryRequest(
            configuration,
            purpose,
            allowList,
            runConfiguration.NoMft,
            []);

        return coordinator.DiscoverAsync(request, updateHandler, cancellationToken);
    }

    internal static void WriteDiscoveryDiagnostics(DiscoveryInventory inventory, TextWriter writer)
    {
        foreach (var (key, error) in inventory.Errors)
        {
            writer.WriteLine($"Discovery error ({key}): {error}");
        }
        foreach (var root in inventory.WatchExcludedRoots)
        {
            writer.WriteLine($"Watch excluded root: {root}");
        }
        foreach (var obs in inventory.Observations)
        {
            if (obs.Source == DiscoverySource.Snapshot && obs.ScanTimestamp.HasValue)
            {
                writer.WriteLine($"Using index snapshot for {obs.Root} (scan timestamp {obs.ScanTimestamp.Value:O}; may be stale)");
            }
        }
    }
}
