using System.Text.Json.Serialization;
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
            await previous.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await SetDiscoveryIndexAsync(null).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public async Task GetRepositoryPathsAsync(ICollection<string> repositoryPaths,
        IUpdateHandler? updateHandler = null, bool noMft = false, bool retainDiscoveryIndex = false)
    {
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = SearchPaths,
            IgnoredPaths = IgnoredPaths,
            SkipHiddenDirectories = SkipHiddenDirectories
        };

        if (retainDiscoveryIndex)
        {
            await using var discoveryResult = await GitWizardApi.TryDiscoverRepositoriesUsingMftAsync(
                configuration, repositoryPaths, updateHandler, noMft).ConfigureAwait(false);
            if (discoveryResult.Success)
            {
                await SetDiscoveryIndexAsync(discoveryResult.DetachIndexSession()).ConfigureAwait(false);
                return;
            }
        }
        else if (await GitWizardApi.TryFindAllRepositoriesUsingMftAsync(
                     configuration, repositoryPaths, updateHandler, noMft).ConfigureAwait(false))
        {
            return;
        }

        var count = 0;
        try
        {
            updateHandler?.StartProgress("Getting GitWizardRepository Paths", SearchPaths.Count);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception thrown by GetRepositoryPaths StartProgress callback.");
        }

        Parallel.ForEach(SearchPaths, path =>
        {
            GitWizardApi.GetRepositoryPaths(path, repositoryPaths, IgnoredPaths, updateHandler);

            try
            {
                updateHandler?.UpdateProgress(Interlocked.Increment(ref count));
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, "Exception thrown by GetRepositoryPaths UpdateProgress callback.");
            }
        });
    }
}
