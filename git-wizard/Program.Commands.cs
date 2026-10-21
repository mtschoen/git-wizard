// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

public static partial class Program
{
    internal static Task<int> RunSweepAsync(
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

    internal static Task<int> RunTagsAsync(
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

    internal static Task<int> RunBranchesAsync(
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
                    notMergedOnly: runConfiguration.NotMergedOnly))),
            new CommandEnvironment(coordinator, stdout, stderr),
            cancellationToken);

    internal static async Task<int> RunScanOnlyAsync(
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
