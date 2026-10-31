using System.Text.Json.Serialization;
using GitWizard.Discovery;
using MFTLib.Index;

namespace GitWizard;

public partial class GitWizardReport
{
    RepositoryDiscoverySession? _discoverySession;

    // Only the Refresh that follows discovery may await and use these indexed sizes.
    Task<IReadOnlyDictionary<string, long>>? _indexedSizes;

    /// <summary>Borrowed query access, valid while the report owns the discovery session.</summary>
    [JsonIgnore]
    public FileIndexSet? DiscoveryIndex => _discoverySession?.IndexSession?.IndexSet;

    public MftIndexSession? DetachDiscoveryIndex() =>
        Interlocked.Exchange(ref _discoverySession, null)?.DetachIndexSession();

    internal async Task SetDiscoveryIndexAsync(MftIndexSession? nextIndex)
    {
        if (nextIndex is null)
        {
            await SetDiscoverySessionAsync(null).ConfigureAwait(false);
            return;
        }

        var previous = Interlocked.Exchange(ref _discoverySession, new RepositoryDiscoverySession(nextIndex));
        if (previous is not null && !ReferenceEquals(previous.IndexSession, nextIndex))
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal Task SetDiscoveryResultAsync(RepositoryDiscoveryResult result) =>
        SetDiscoverySessionAsync(result.DetachSession());

    async Task SetDiscoverySessionAsync(RepositoryDiscoverySession? nextSession)
    {
        var previous = Interlocked.Exchange(ref _discoverySession, nextSession);
        if (previous is not null && !ReferenceEquals(previous, nextSession))
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }
    }

    async Task CompleteDiscoverySizesAsync()
    {
        if (_discoverySession is { } session)
        {
            await session.WaitForRepositorySizesAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await SetDiscoverySessionAsync(null).ConfigureAwait(false);
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
            await SetDiscoveryResultAsync(result).ConfigureAwait(false);
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
        _indexedSizes = result.RepositorySizes;
        return result;
    }
}
