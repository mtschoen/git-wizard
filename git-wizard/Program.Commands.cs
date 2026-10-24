using GitWizard.Discovery;
// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

public static partial class Program
{
    static Task<int> RunSweepAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator? coordinator = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default) =>
        RunInventoryCommandAsync(
            runConfiguration,
            configuration,
            new InventoryCommand<RepositorySweepReport>(
                IncludeLinkedClones: false,
                (paths, _) => Task.FromResult(RepositorySweepReport.Scan(paths))),
            new CommandEnvironment(coordinator, stdout, stderr),
            cancellationToken);

    static Task<int> RunTagsAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator? coordinator = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default) =>
        RunInventoryCommandAsync(
            runConfiguration,
            configuration,
            new InventoryCommand<TagInventoryReport>(
                IncludeLinkedClones: true,
                (paths, scanToken) => TagInventoryReport.ScanAsync(
                    paths, matchPattern: runConfiguration.TagMatchPattern ?? "*", cancellationToken: scanToken)),
            new CommandEnvironment(coordinator, stdout, stderr),
            cancellationToken);

    static Task<int> RunBranchesAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator? coordinator = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default) =>
        RunInventoryCommandAsync(
            runConfiguration,
            configuration,
            new InventoryCommand<BranchInventoryReport>(
                IncludeLinkedClones: true,
                (paths, _) => Task.FromResult(BranchInventoryReport.Scan(
                    paths,
                    baseBranchName: runConfiguration.NotMergedBranch,
                    notMergedOnly: runConfiguration.NotMergedOnly,
                    localOnly: runConfiguration.LocalOnly))),
            new CommandEnvironment(coordinator, stdout, stderr),
            cancellationToken);

    static Task<int> RunPruneAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator? coordinator = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        var restoreHints = stderr ?? Console.Error;
        var options = new PruneOptions(
            Branches: runConfiguration.PruneBranches,
            Stashes: runConfiguration.PruneStashes,
            Apply: runConfiguration.PruneApply,
            BaseBranchName: runConfiguration.PruneBaseBranch);
        return RunInventoryCommandAsync(
            runConfiguration,
            configuration,
            new InventoryCommand<PruneReport>(
                IncludeLinkedClones: true,
                (paths, _) => Task.FromResult(Pruner.Run(paths, options, restoreHints))),
            new CommandEnvironment(coordinator, stdout, stderr),
            cancellationToken);
    }

    static async Task<int> RunScanOnlyAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator? coordinator = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        GitWizardLog.LogMethod = Console.Error.WriteLine;

        if (!EnsureUsableSearchPaths(configuration, _ => { }))
        {
            return 1;
        }

        await using var result = await DiscoverForCommandAsync(
            runConfiguration, configuration, coordinator, cancellationToken: cancellationToken).ConfigureAwait(false);

        WriteDiscoveryDiagnostics(result.Inventory, stderr);

        foreach (var path in result.Inventory.Paths.Where(path => MatchesFilter(path, runConfiguration.FilterPattern)))
        {
            await stdout.WriteLineAsync(path).ConfigureAwait(false);
        }

        return result.Inventory.CanPublishFullInventory ? 0 : 1;
    }
}
