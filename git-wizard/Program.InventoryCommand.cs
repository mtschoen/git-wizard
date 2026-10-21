using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitWizard.CLI;

public static partial class Program
{
    static readonly ConcurrentDictionary<(bool Minified, JsonIgnoreCondition IgnoreCondition), JsonSerializerOptions> SerializerOptionsByShape = new();

    /// <summary>
    /// True when the path contains the pattern, ignoring case; a null or empty pattern matches every path.
    /// </summary>
    internal static bool MatchesFilter(string path, string? filterPattern) =>
        string.IsNullOrEmpty(filterPattern) || path.Contains(filterPattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The JSON options for command output: indented unless minified, and omitting null values
    /// (inventory commands) or default values (the report) as <paramref name="ignoreCondition"/> says.
    /// </summary>
    static JsonSerializerOptions GetSerializerOptions(RunConfiguration runConfiguration, JsonIgnoreCondition ignoreCondition) =>
        SerializerOptionsByShape.GetOrAdd(
            (runConfiguration.Minified, ignoreCondition),
            shape => new JsonSerializerOptions
            {
                WriteIndented = !shape.Minified,
                DefaultIgnoreCondition = shape.IgnoreCondition
            });

    internal static string SerializeInventoryReport<TReport>(RunConfiguration runConfiguration, TReport report) =>
        JsonSerializer.Serialize(report, GetSerializerOptions(runConfiguration, JsonIgnoreCondition.WhenWritingNull));

    /// <summary>
    /// What distinguishes one inventory command: whether its inventory includes linked worktrees and
    /// submodule checkouts, and how it builds its report from the filtered repository paths.
    /// </summary>
    internal sealed record InventoryCommand<TReport>(
        bool IncludeLinkedClones,
        Func<IReadOnlyList<string>, CancellationToken, Task<TReport>> ScanAsync);

    /// <summary>
    /// The discovery coordinator and output writers a command runs against; null members default
    /// to a fresh coordinator and the process console.
    /// </summary>
    internal sealed record CommandEnvironment(
        RepositoryDiscoveryCoordinator? Coordinator = null,
        TextWriter? Stdout = null,
        TextWriter? Stderr = null);

    /// <summary>
    /// The skeleton shared by the sweep, tags and branches commands: silence the console log,
    /// discover repositories, apply the filter, build the report, then serialize it to the save
    /// path and stdout.
    /// </summary>
    static async Task<int> RunInventoryCommandAsync<TReport>(
        RunConfiguration runConfiguration,
        GitWizardConfiguration configuration,
        InventoryCommand<TReport> command,
        CommandEnvironment environment,
        CancellationToken cancellationToken)
    {
        var stdout = environment.Stdout ?? Console.Out;
        var stderr = environment.Stderr ?? Console.Error;

        GitWizardLog.SilentMode = true;
        GitWizardLog.LogMethod = Console.Error.WriteLine;

        var explicitPaths = ParseExplicitPaths(runConfiguration);
        var isFullInventory = explicitPaths == null;

        await using var result = await DiscoverForCommandAsync(
            runConfiguration, configuration, environment.Coordinator, explicitPaths, command.IncludeLinkedClones, cancellationToken).ConfigureAwait(false);

        var exitCode = GetDiscoveryExitCode(result.Inventory, isFullInventory);
        if (exitCode is { } failureExitCode)
        {
            WriteDiscoveryDiagnostics(result.Inventory, stderr);
            return failureExitCode;
        }

        var repositoryPaths = result.Inventory.Paths
            .Where(path => MatchesFilter(path, runConfiguration.FilterPattern))
            .ToArray();

        var report = await command.ScanAsync(repositoryPaths, cancellationToken).ConfigureAwait(false);
        var json = SerializeInventoryReport(runConfiguration, report);
        if (runConfiguration.SavePath != null)
        {
            await File.WriteAllTextAsync(runConfiguration.SavePath, json, cancellationToken).ConfigureAwait(false);
        }

        if (!runConfiguration.Silent)
        {
            await stdout.WriteLineAsync(json).ConfigureAwait(false);
        }

        return 0;
    }
}
