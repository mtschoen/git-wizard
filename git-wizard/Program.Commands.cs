using System.Text.Json;

// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

public static partial class Program
{
    internal static async Task<int> RunSweepAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator? coordinator = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        GitWizardLog.SilentMode = true;
        GitWizardLog.LogMethod = Console.Error.WriteLine;

        var explicitPaths = ParseExplicitPaths(runConfiguration);
        var isFullInventory = explicitPaths == null;

        IReadOnlyList<string> repositoryPaths;
        if (explicitPaths != null)
        {
            repositoryPaths = explicitPaths;
        }
        else
        {
            coordinator ??= new RepositoryDiscoveryCoordinator();
            await using var result = await DiscoverForCommandAsync(
                runConfiguration, configuration, coordinator, null, cancellationToken).ConfigureAwait(false);

            var exitCode = GetDiscoveryExitCode(result.Inventory, isFullInventory);
            if (exitCode is { } failureExitCode)
            {
                WriteDiscoveryDiagnostics(result.Inventory, stderr);
                return failureExitCode;
            }

            repositoryPaths = result.Inventory.Paths;
        }

        if (!string.IsNullOrEmpty(runConfiguration.FilterPattern))
        {
            repositoryPaths = repositoryPaths
                .Where(path => path.Contains(runConfiguration.FilterPattern, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var report = RepositorySweepReport.Scan(repositoryPaths);
        var json = SerializeSweepReport(runConfiguration, report);
        if (runConfiguration.SavePath != null)
            await File.WriteAllTextAsync(runConfiguration.SavePath, json, cancellationToken).ConfigureAwait(false);

        if (!runConfiguration.Silent)
            await stdout.WriteLineAsync(json).ConfigureAwait(false);

        return 0;
    }

    internal static async Task<int> RunTagsAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator? coordinator = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        GitWizardLog.SilentMode = true;
        GitWizardLog.LogMethod = Console.Error.WriteLine;

        var explicitPaths = ParseExplicitPaths(runConfiguration);
        var isFullInventory = explicitPaths == null;

        coordinator ??= new RepositoryDiscoveryCoordinator();
        await using var result = await GitWizardApi.DiscoverAllCloneInventoryAsync(
            configuration,
            explicitPaths,
            noMft: runConfiguration.NoMft,
            coordinator: coordinator,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var exitCode = GetDiscoveryExitCode(result.Inventory, isFullInventory);
        if (exitCode is { } failureExitCode)
        {
            WriteDiscoveryDiagnostics(result.Inventory, stderr);
            return failureExitCode;
        }

        var allClones = result.Inventory.Paths;
        if (!string.IsNullOrEmpty(runConfiguration.FilterPattern))
        {
            allClones = allClones
                .Where(path => path.Contains(runConfiguration.FilterPattern, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var report = await TagInventoryReport.ScanAsync(
            allClones,
            matchPattern: runConfiguration.TagMatchPattern ?? "*",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var options = runConfiguration.Minified ? CompactSweepSerializerOptions : IndentedSweepSerializerOptions;
        var json = JsonSerializer.Serialize(report, options);

        if (runConfiguration.SavePath != null)
            await File.WriteAllTextAsync(runConfiguration.SavePath, json, cancellationToken).ConfigureAwait(false);

        if (!runConfiguration.Silent)
            await stdout.WriteLineAsync(json).ConfigureAwait(false);

        return 0;
    }

    internal static async Task<int> RunBranchesAsync(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator? coordinator = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        GitWizardLog.SilentMode = true;
        GitWizardLog.LogMethod = Console.Error.WriteLine;

        var explicitPaths = ParseExplicitPaths(runConfiguration);
        var isFullInventory = explicitPaths == null;

        coordinator ??= new RepositoryDiscoveryCoordinator();
        await using var result = await GitWizardApi.DiscoverAllCloneInventoryAsync(
            configuration,
            explicitPaths,
            noMft: runConfiguration.NoMft,
            coordinator: coordinator,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var exitCode = GetDiscoveryExitCode(result.Inventory, isFullInventory);
        if (exitCode is { } failureExitCode)
        {
            WriteDiscoveryDiagnostics(result.Inventory, stderr);
            return failureExitCode;
        }

        var allClones = result.Inventory.Paths;
        if (!string.IsNullOrEmpty(runConfiguration.FilterPattern))
        {
            allClones = allClones
                .Where(path => path.Contains(runConfiguration.FilterPattern, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var report = BranchInventoryReport.Scan(
            allClones,
            baseBranchName: runConfiguration.NotMergedBranch,
            notMergedOnly: runConfiguration.NotMergedOnly);

        var options = runConfiguration.Minified ? CompactSweepSerializerOptions : IndentedSweepSerializerOptions;
        var json = JsonSerializer.Serialize(report, options);

        if (runConfiguration.SavePath != null)
            await File.WriteAllTextAsync(runConfiguration.SavePath, json, cancellationToken).ConfigureAwait(false);

        if (!runConfiguration.Silent)
            await stdout.WriteLineAsync(json).ConfigureAwait(false);

        return 0;
    }

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
            return 1;

        coordinator ??= new RepositoryDiscoveryCoordinator();
        await using var result = await DiscoverForCommandAsync(
            runConfiguration, configuration, coordinator, null, cancellationToken).ConfigureAwait(false);

        WriteDiscoveryDiagnostics(result.Inventory, stderr);

        foreach (var path in result.Inventory.Paths)
        {
            if (string.IsNullOrEmpty(runConfiguration.FilterPattern) ||
                path.Contains(runConfiguration.FilterPattern, StringComparison.OrdinalIgnoreCase))
            {
                await stdout.WriteLineAsync(path).ConfigureAwait(false);
            }
        }

        return result.Inventory.CanPublishFullInventory ? 0 : 1;
    }

    static string SerializeSweepReport(RunConfiguration runConfiguration, RepositorySweepReport report)
    {
        var options = runConfiguration.Minified
            ? CompactSweepSerializerOptions
            : IndentedSweepSerializerOptions;
        return JsonSerializer.Serialize(report, options);
    }
}
