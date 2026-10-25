using GitWizard.Discovery;
namespace GitWizard.Watch;

public sealed partial class RepositoryWatchService
{
    readonly WatchStartupResult? _startup;

    public event Action<DiscoveryInventory>? InventoryReady;

    public RepositoryWatchService(
        WatchStartupResult startup,
        TimeSpan? debounce = null)
        : this(
            (startup ?? throw new ArgumentNullException(nameof(startup))).Source ?? throw new ArgumentException("Cannot start watch service with null source.", nameof(startup)),
            startup.Inventory.Paths,
            startup.SearchRoots,
            debounce,
            startup.IncludeTempDirectories,
            startup.IgnoredRoots)
    {
        _startup = startup;
    }
}
