namespace GitWizard.CLI;

public static partial class Program
{
    internal static int? GetDiscoveryExitCode(DiscoveryInventory? inventory, bool fullInventory)
    {
        return fullInventory && inventory is { CanPublishFullInventory: false } ? 3 : null;
    }

    /// <summary>
    /// The one discovery route for the commands. Explicit paths bypass discovery; with
    /// <paramref name="includeLinkedClones"/> the inventory also lists each repository's linked
    /// worktrees and submodule checkouts (the tags and branches commands).
    /// </summary>
    internal static Task<RepositoryDiscoveryResult> DiscoverForCommandAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator? coordinator = null,
        ICollection<string>? explicitPaths = null,
        bool includeLinkedClones = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (includeLinkedClones)
        {
            return GitWizardApi.DiscoverAllCloneInventoryAsync(
                configuration,
                explicitPaths,
                runConfiguration.NoMft,
                coordinator ?? new RepositoryDiscoveryCoordinator(),
                updateHandler: null,
                cancellationToken);
        }

        if (explicitPaths != null)
        {
            return Task.FromResult(new RepositoryDiscoveryResult(CreateExplicitPathInventory(explicitPaths)));
        }

        coordinator ??= new RepositoryDiscoveryCoordinator();

        var rebuild = runConfiguration.RebuildRepositoryList || runConfiguration.Command is CommandKind.ScanOnly or CommandKind.Sweep;
        var purpose = rebuild
            ? DiscoveryPurpose.Rebuild
            : DiscoveryPurpose.Ordinary;

        var allowList = !rebuild;

        var request = new DiscoveryRequest(
            configuration,
            purpose,
            allowList,
            runConfiguration.NoMft,
            []);

        return coordinator.DiscoverAsync(request, null, cancellationToken);
    }

    static DiscoveryInventory CreateExplicitPathInventory(ICollection<string> explicitPaths) => new()
    {
        Outcome = DiscoveryOutcome.CompleteNonempty,
        Route = DiscoveryRoute.RecursiveOnly,
        Paths = explicitPaths.ToArray(),
        RequestedNtfsDrives = [],
        Observations = [],
        DriveStatuses = [],
        Errors = new Dictionary<string, string>(),
        WatchExcludedRoots = []
    };

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
