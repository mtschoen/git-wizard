using System.Diagnostics;
using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    // Test seam: lets a test drive refresh discovery through a coordinator backed by fake
    // dependencies instead of the real platform index. Null in production.
    internal RepositoryDiscoveryCoordinator? RefreshDiscoveryCoordinator { get; set; }

    // GITWIZARD_NO_MFT=1 walks the filesystem instead of reading the MFT, so no elevated broker (UAC
    // prompt) is launched. scripts/update-screenshots.ps1 sets it for unattended headless captures.
    internal static bool NoMftRequested => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITWIZARD_NO_MFT"));

    /// <summary>
    /// Hard refresh: force a fresh discovery scan (rebuild purpose), then refresh. The cached
    /// repository list and report are left in place until the scan's outcome is known - an
    /// unusable or partial discovery preserves them rather than clearing them up front.
    /// Bound to Shift+click on the Refresh button.
    /// </summary>
    public async Task HardRefreshAsync()
    {
        if (IsRefreshing)
            return;

        await RefreshAsync(background: false, rebuild: true).ConfigureAwait(false);
    }

    public async Task RefreshAsync(bool background = false, bool fetchRemotes = false, bool rebuild = false)
    {
        if (IsRefreshing)
            return;

        IsRefreshing = true;
        IsScanning = true;
        StatusHeader.ShowRefreshMessage(fetchRemotes ? "Fetching remotes and refreshing..." : "Refreshing...");

        await EnsureGlobalUserEmailAsync();

        // Read cached repo paths and config first (lightweight file I/O).
        // Pre-populate UI from cached report so repos appear immediately while the
        // full refresh runs in the background. This eliminates the 30+ second empty-list
        // gap on large repo sets where individual refreshes take a long time (issue #98).
        var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync().ConfigureAwait(false);
        var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync().ConfigureAwait(false);
        var cachedReport = await GitWizardReport.GetCachedReportAsync().ConfigureAwait(false);

        // A rebuild runs discovery instead of re-checking the cached paths; the cached paths
        // still drive pre-population, and the coordinator's inventory decides what is persisted.
        var repositoryPaths = rebuild ? null : cachedPaths;

        // Clear displayed list, group state, and the backing collections so that only
        // nodes from the cached report (pre-population) + fresh scan (AddRepository)
        // survive. This prevents stale or deleted repos from accumulating across
        // repeated refresh cycles. The visible collection must be changed on the UI thread.
        await _ui.InvokeAsync(() =>
        {
            RepositoryList.ClearRepositories();
            RepositoryList.PrePopulateFromReport(configuration, cachedPaths, cachedReport);
        }).ConfigureAwait(false);

        var (deletedPaths, renamedOldPaths, nonRepositoryPaths, freshPaths, publishable) =
            await RunRefreshScanAsync(configuration, repositoryPaths, fetchRemotes, rebuild).ConfigureAwait(false);

        await UpdateCachedPathsAfterScanAsync(deletedPaths, renamedOldPaths, nonRepositoryPaths).ConfigureAwait(false);

        // Wait for the UI command queue to fully drain before pruning. RepositoryCreated
        // commands are queued before a refresh determines that a path is no longer a repo,
        // so pruning earlier could let a late command restore a stale row.
        if (!await WaitForUICommandsToDrainAsync().ConfigureAwait(false))
            return;

        await _ui.InvokeAsync(() =>
        {
            // Only a publishable inventory observed every configured root, so only then may
            // pre-populated rows be pruned for missing from the fresh scan. An incomplete
            // discovery retains the rows its scope did not cover.
            if (publishable)
                RepositoryList.PruneStalePrePopulatedRepos(freshPaths, renamedOldPaths);
            RepositoryList.RemoveRenamedReposFromUi(renamedOldPaths);
        }).ConfigureAwait(false);

        // CompleteRefresh runs one sorted rebuild (ApplyFilterAndGrouping) and swaps in the sorted
        // collection while preserving selection and scroll position.
        CompleteRefresh();
    }

    internal void CompleteRefresh()
    {
        RepositoryList.ApplyFilterAndGrouping(suppressAnchorCapture: true);
        IsScanning = false;
        IsRefreshing = false;
    }

    // Drop deleted, renamed (old path), and stale non-repo entries from the cached
    // repository-path list.
    internal static async Task UpdateCachedPathsAfterScanAsync(HashSet<string> deletedPaths, HashSet<string> renamedOldPaths,
        HashSet<string> nonRepositoryPaths)
    {
        var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync().ConfigureAwait(false);
        if (cachedPaths == null)
        {
            return;
        }

        var pathsToRemove = deletedPaths.Union(renamedOldPaths).Union(nonRepositoryPaths).ToHashSet();
        if (pathsToRemove.Count > 0)
        {
            var updatedPaths = cachedPaths.Where(p => !pathsToRemove.Contains(p)).ToList();
            await GitWizardApi.SaveCachedRepositoryPathsAsync(updatedPaths).ConfigureAwait(false);
        }
    }

    // Read the global git user.email once per session for the "My Repositories" filter.
    static async Task EnsureGlobalUserEmailAsync()
    {
        if (GlobalUserEmail != null)
            return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "config --global user.email",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
                GlobalUserEmail = (await process.StandardOutput.ReadToEndAsync()).Trim();
        }
        catch (Exception exception)
        {
            // Could not read global git user.email; the email filter just will not match.
            GitWizardLog.Log($"Could not read global git user.email: {exception.Message}", GitWizardLog.LogType.Verbose);
        }
    }

    // Run the CPU-bound git scan on the thread pool; returns the deleted, renamed-old, and stale
    // non-repo paths, the set of healthy repo paths discovered by the scan, and whether the
    // discovery inventory may publish a full inventory (false for partial/unusable outcomes).
    async Task<(HashSet<string> DeletedPaths, HashSet<string> RenamedOldPaths, HashSet<string> NonRepositoryPaths, HashSet<string> FreshPaths, bool Publishable)> RunRefreshScanAsync(
        GitWizardConfiguration configuration, string[]? repositoryPaths, bool fetchRemotes, bool rebuild = false)
    {
        var deletedPaths = new HashSet<string>();
        var renamedOldPaths = new HashSet<string>();
        var nonRepositoryPaths = new HashSet<string>();
        var freshPaths = new HashSet<string>();
        var publishable = true;

        await Task.Run(async () =>
        {
            _stopwatch.Restart();
            await using var report = await GitWizardReport.GenerateReportAsync(configuration, repositoryPaths, this,
                new GitWizardReportOptions
                {
                    FetchRemotes = fetchRemotes,
                    DeepRefresh = fetchRemotes,
                    NoMft = NoMftRequested,
                    RetainDiscoveryIndex = true,
                    DiscoveryPurpose = rebuild ? DiscoveryPurpose.Rebuild : DiscoveryPurpose.Ordinary,
                    DiscoveryCoordinator = RefreshDiscoveryCoordinator
                }).ConfigureAwait(false);
            _stopwatch.Stop();

            await PreparedDiscoveryIndex.SetAsync(report.DetachDiscoveryIndex()).ConfigureAwait(false);

            publishable = report.DiscoveryInventory is not { CanPublishFullInventory: false };

            // Capture deleted and stale non-repo paths for cache cleanup on main thread
            deletedPaths = new HashSet<string>(report.DeletedPaths);
            nonRepositoryPaths = new HashSet<string>(report.NonRepositoryPaths);
            freshPaths = new HashSet<string>(report.Repositories.Keys);

            // Detect renamed repos: find errored repos whose remote URLs
            // match a newly discovered (non-error) repo at a different path
            var erroredRepos = report.Repositories
                .Where(kvp => kvp.Value.RefreshError != null)
                .ToList();
            var healthyRepos = report.Repositories
                .Where(kvp => kvp.Value.RefreshError == null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            foreach (var (path, repo) in erroredRepos)
            {
                var newHealthyPath = RepositoryGroupKeys.FindRenamedRepo(repo, path, healthyRepos);
                if (newHealthyPath != null && newHealthyPath != path)
                {
                    renamedOldPaths.Add(path);
                    GitWizardLog.Log($"Repository renamed: {path} -> {newHealthyPath}");
                }
            }

            var refreshMsg = $"Refresh completed in {(float)_stopwatch.ElapsedMilliseconds / 1000:F2} seconds";
            if (deletedPaths.Count > 0)
                refreshMsg += $", {deletedPaths.Count} deleted";
            if (renamedOldPaths.Count > 0)
                refreshMsg += $", {renamedOldPaths.Count} renamed";
            if (nonRepositoryPaths.Count > 0)
                refreshMsg += $", {nonRepositoryPaths.Count} stale";
            if (!publishable)
            {
                refreshMsg += " - discovery incomplete; previous entries preserved";
                LogError("Discovery was incomplete; the previous repository list and report were preserved.");
            }
            StatusHeader.LastRefreshMessage = refreshMsg;

            // Only a publishable discovery may replace the cached list; an unusable or partial
            // outcome leaves the coordinator's reconciled list (and the prior entries) intact.
            if (repositoryPaths == null && publishable)
                await GitWizardApi.SaveCachedRepositoryPathsAsync(report.GetRepositoryPaths()).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return (deletedPaths, renamedOldPaths, nonRepositoryPaths, freshPaths, publishable);
    }
}
