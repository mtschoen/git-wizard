using LibGit2Sharp;

namespace GitWizard;

public static partial class GitWizardApi
{
    /// <summary>
    /// Path to the standard pr-crew clones directory (~/.pr-crew/repos).
    /// </summary>
    public static string GetPrCrewReposPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pr-crew", "repos");
    }

    /// <summary>
    /// Discovers all git clones across the machine or explicit paths, expanding to include:
    /// - Standard repositories discovered across all search paths
    /// - Repositories in ~/.pr-crew/repos when present
    /// - Submodule checkouts (including nested submodules)
    /// - Linked worktrees (including worktree submodules)
    /// </summary>
    public static async Task<IReadOnlyList<string>> DiscoverAllClonePathsAsync(
        GitWizardConfiguration configuration,
        ICollection<string>? explicitPaths = null,
        bool noMft = false,
        IUpdateHandler? updateHandler = null)
    {
        var coordinator = new RepositoryDiscoveryCoordinator();
        await using var result = await DiscoverAllCloneInventoryAsync(
            configuration, explicitPaths, noMft, coordinator, updateHandler, CancellationToken.None).ConfigureAwait(false);
        return result.Inventory.Paths;
    }

    /// <summary>
    /// Discovers all git clones across the machine or explicit paths with full inventory and completeness tracking.
    /// </summary>
    public static async Task<RepositoryDiscoveryResult> DiscoverAllCloneInventoryAsync(
        GitWizardConfiguration configuration,
        ICollection<string>? explicitPaths,
        bool noMft,
        RepositoryDiscoveryCoordinator coordinator,
        IUpdateHandler? updateHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(coordinator);

        if (explicitPaths?.Count > 0)
        {
            var expandedPaths = ExpandAllClonePaths(explicitPaths);
            var inventory = new DiscoveryInventory
            {
                Outcome = DiscoveryOutcome.CompleteNonempty,
                Route = DiscoveryRoute.RecursiveOnly,
                Paths = expandedPaths,
                RequestedNtfsDrives = [],
                Observations = [],
                DriveStatuses = [],
                Errors = new Dictionary<string, string>(),
                WatchExcludedRoots = []
            };
            return new RepositoryDiscoveryResult(inventory);
        }

        var searchConfig = new GitWizardConfiguration
        {
            SearchPaths = new SortedSet<string>(configuration.SearchPaths, StringComparer.Ordinal),
            IgnoredPaths = new SortedSet<string>(configuration.IgnoredPaths, StringComparer.Ordinal),
            SkipHiddenDirectories = configuration.SkipHiddenDirectories,
            IncludeTempDirectories = configuration.IncludeTempDirectories,
            ForkPath = configuration.ForkPath
        };

        var prCrewReposPath = GetPrCrewReposPath();
        if (Directory.Exists(prCrewReposPath))
        {
            var normalizedPrCrewPath = NormalizePath(prCrewReposPath);
            var alreadyCovered = searchConfig.SearchPaths
                .Select(ExpandSearchPath)
                .Any(searchPath => !string.IsNullOrEmpty(searchPath) && IsSameOrChildPath(normalizedPrCrewPath, searchPath));

            if (!alreadyCovered)
            {
                searchConfig.SearchPaths.Add(prCrewReposPath);
            }
        }

        var request = new DiscoveryRequest(
            searchConfig,
            DiscoveryPurpose.Ordinary,
            false,
            noMft,
            []);

        var discoveryResult = await coordinator.DiscoverAsync(request, updateHandler, cancellationToken).ConfigureAwait(false);
        var expandedClonePaths = ExpandAllClonePaths(discoveryResult.Inventory.Paths);

        var expandedInventory = discoveryResult.Inventory with { Paths = expandedClonePaths };

        var session = discoveryResult.DetachIndexSession();
        await discoveryResult.DisposeAsync().ConfigureAwait(false);
        return new RepositoryDiscoveryResult(expandedInventory, session);
    }

    /// <summary>
    /// Takes a collection of repository roots and expands it to include all linked
    /// worktrees and initialized submodule checkouts (recursing nested submodules/worktrees).
    /// </summary>
    public static IReadOnlyList<string> ExpandAllClonePaths(IEnumerable<string> repositoryPaths)
    {
        ArgumentNullException.ThrowIfNull(repositoryPaths);

        var allClones = new HashSet<string>(PathComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        foreach (var path in repositoryPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            CollectCloneAndDescendants(path, allClones);
        }

        return allClones.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    static void CollectCloneAndDescendants(string repositoryPath, HashSet<string> allClones)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return;
        }

        var normalizedPath = NormalizePath(repositoryPath);
        if (allClones.Contains(normalizedPath))
        {
            return;
        }

        try
        {
            if (!Repository.IsValid(repositoryPath))
            {
                return;
            }

            allClones.Add(normalizedPath);

            using var repository = new Repository(repositoryPath);

            // 1. Enumerate worktrees
            try
            {
                foreach (var worktree in repository.Worktrees)
                {
                    try
                    {
                        if (worktree == null)
                        {
                            continue;
                        }

                        using var worktreeRepository = worktree.WorktreeRepository;
                        var worktreePath = worktreeRepository?.Info?.WorkingDirectory;

                        if (!string.IsNullOrEmpty(worktreePath))
                        {
                            CollectCloneAndDescendants(worktreePath, allClones);
                        }
                    }
                    catch (Exception exception)
                    {
                        GitWizardLog.LogException(exception, $"Exception reading worktree for {repositoryPath}");
                    }
                }
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, $"Exception enumerating worktrees for {repositoryPath}");
            }

            // 2. Enumerate submodules
            try
            {
                var workingDirectory = repository.Info.WorkingDirectory ?? repositoryPath;
                foreach (var submodule in repository.Submodules)
                {
                    try
                    {
                        if (submodule == null || submodule.WorkDirCommitId == null)
                        {
                            continue;
                        }

                        var submodulePath = Path.Combine(workingDirectory, submodule.Path);
                        CollectCloneAndDescendants(submodulePath, allClones);
                    }
                    catch (Exception exception)
                    {
                        GitWizardLog.LogException(exception, $"Exception reading submodule for {repositoryPath}");
                    }
                }
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, $"Exception enumerating submodules for {repositoryPath}");
            }
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception opening repository at {repositoryPath}");
        }
    }
}
