using System.Text.Json.Serialization;
using GitWizard.Discovery;
using MFTLib.Index;

namespace GitWizard;

public partial class GitWizardReport
{
    MftIndexSession? _discoveryIndex;

    /// <summary>Borrowed query access, valid while the report owns the discovery session.</summary>
    [JsonIgnore]
    public FileIndex? DiscoveryIndex => _discoveryIndex?.Index;

    public MftIndexSession? DetachDiscoveryIndex() => Interlocked.Exchange(ref _discoveryIndex, null);

    internal async Task SetDiscoveryIndexAsync(MftIndexSession? nextIndex)
    {
        var previous = Interlocked.Exchange(ref _discoveryIndex, nextIndex);
        if (previous is not null && !ReferenceEquals(previous, nextIndex))
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await SetDiscoveryIndexAsync(null).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public Task GetRepositoryPathsAsync(ICollection<string> repositoryPaths,
        IUpdateHandler? updateHandler = null, bool noMft = false, bool retainDiscoveryIndex = false) =>
        GetRepositoryPathsAsync(repositoryPaths, updateHandler, noMft, retainDiscoveryIndex, null, CancellationToken.None);

    public async Task GetRepositoryPathsAsync(ICollection<string> repositoryPaths,
        IUpdateHandler? updateHandler, bool noMft, bool retainDiscoveryIndex,
        GitWizardReportOptions? options, CancellationToken cancellationToken = default)
    {
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = SearchPaths,
            IgnoredPaths = IgnoredPaths,
            SkipHiddenDirectories = SkipHiddenDirectories,
            IncludeTempDirectories = IncludeTempDirectories
        };

        var coordinator = options?.DiscoveryCoordinator ?? new RepositoryDiscoveryCoordinator();
        var purpose = options?.DiscoveryPurpose ?? DiscoveryPurpose.Ordinary;
        var allowList = options?.AllowRepositoryList ?? false;

        var request = new DiscoveryRequest(configuration, purpose, allowList, noMft, []);
        var result = await DiscoverRepositoryPathsAsync(request, coordinator, updateHandler, cancellationToken).ConfigureAwait(false);

        foreach (var path in result.Inventory.Paths)
        {
            repositoryPaths.Add(path);
        }

        if (retainDiscoveryIndex)
        {
            await SetDiscoveryIndexAsync(result.DetachIndexSession()).ConfigureAwait(false);
        }
        else
        {
            await result.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<RepositoryDiscoveryResult> DiscoverRepositoryPathsAsync(
        DiscoveryRequest request,
        RepositoryDiscoveryCoordinator coordinator,
        IUpdateHandler? updateHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(coordinator);

        var result = await coordinator.DiscoverAsync(request, updateHandler, cancellationToken).ConfigureAwait(false);
        DiscoveryInventory = result.Inventory;
        return result;
    }
}
