using GitWizard;
using MFTLib.Index;

namespace GitWizardTests.TestSupport;

/// <summary>Runs the production candidate selection for one logical search root and returns the repository paths.</summary>
static class CandidateSelection
{
    public static string[] Paths(
        FileIndex index, string root, string[] ignoredPaths, bool? skipHiddenDirectories = null,
        bool? includeTempDirectories = null)
    {
        var configuration = new GitWizardConfiguration
        {
            IncludeTempDirectories = includeTempDirectories,
            SkipHiddenDirectories = skipHiddenDirectories
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);
        var scope = DiscoveryScope.CreateExpanded(
            request, [root], [], ignoredPaths, _ => new RootClassification(root, RootFileSystem.Ntfs, "X", null));
        return [.. GitWizardApi.SelectRepositoryCandidates(index, scope).Select(candidate => candidate.Path)];
    }

    public static async Task<IReadOnlyList<string>> InventoryAsync(
        string searchRoot, string retainedRoot, string? ignoredRoot, bool skipHiddenDirectories, bool includeTempDirectories)
    {
        Directory.CreateDirectory(searchRoot);
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = false,
            IsLinux = true,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Recursive, null, null),
            CrawlRootAsync = (_, _, _) => Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())),
            OpenIndexAsync = (_, _, _, _) => throw new PlatformNotSupportedException(),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => new ScriptedVolumeChangeSource { KeepWatching = true }
        });
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { searchRoot },
            IncludeTempDirectories = includeTempDirectories,
            SkipHiddenDirectories = skipHiddenDirectories
        };
        if (ignoredRoot is not null)
        {
            configuration.IgnoredPaths.Add(ignoredRoot);
        }

        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false, [retainedRoot]);
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        await using var startup = await coordinator.PrepareWatchAsync(request, null, cancellation.Token);
        return startup.Inventory.Paths;
    }
}
