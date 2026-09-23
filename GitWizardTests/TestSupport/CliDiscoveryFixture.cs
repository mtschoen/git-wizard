using System.Diagnostics;
using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests.TestSupport;

internal sealed class CliDiscoveryFixture : IAsyncDisposable
{
    readonly string _command;
    readonly string[] _extraArgs;
    bool _createdZShared;
    bool _substMapped;

    public DiscoveryDependencyRecorder Dependencies { get; } = new();
    public IReadOnlyList<IReadOnlyList<string>> OpenedDriveSets => Dependencies.OpenedDriveSets;
    public StringWriter Stdout { get; } = new();
    public StringWriter Stderr { get; } = new();
    public string TempHome { get; }
    public GitWizardConfiguration Configuration { get; set; }
    public bool NoMft { get; set; }
    public int ExitCode { get; private set; }

    public CliDiscoveryFixture(string command, bool noMft = false, params string[] extraArgs)
    {
        _command = command;
        NoMft = noMft;
        _extraArgs = extraArgs;

        GitWizardLog.SilentMode = true;
        TempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();

        EnsureZShared();

        var cPath = Dependencies.IndexFixture.TreePath;
        var zPath = @"Z:\shared";
        Configuration = new GitWizardConfiguration
        {
            SearchPaths = { cPath, zPath }
        };
    }

    void EnsureZShared()
    {
        try
        {
            if (!Directory.Exists(@"Z:\") && OperatingSystem.IsWindows())
            {
                var tempZ = Path.Combine(Path.GetTempPath(), "gw-z-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempZ);
                var psi = new ProcessStartInfo("subst", $"Z: \"{tempZ}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(2000);
                _substMapped = true;
            }

            if (Directory.Exists(@"Z:\") && !Directory.Exists(@"Z:\shared"))
            {
                Directory.CreateDirectory(@"Z:\shared");
                _createdZShared = true;
            }
        }
        catch
        {
            // Best effort
        }
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var argsList = new List<string>();
        switch (_command.ToLowerInvariant())
        {
            case "report":
                break;
            case "scan":
                argsList.Add("--scan-only");
                break;
            case "sweep":
                argsList.Add("--sweep");
                break;
            case "tags":
                argsList.Add("tags");
                break;
            case "branches":
                argsList.Add("branches");
                break;
            case "watch":
                argsList.Add("--watch");
                break;
            default:
                argsList.Add(_command);
                break;
        }

        if (NoMft)
        {
            argsList.Add("--no-mft");
        }

        argsList.AddRange(_extraArgs);

        var parseResult = CliParser.Parse(argsList.ToArray());
        var runConfig = new Program.RunConfiguration(parseResult);

        var coordinator = new RepositoryDiscoveryCoordinator(Dependencies.BuildDependencies());
        ExitCode = await Program.RunCommandCoreAsync(
            runConfig,
            Configuration,
            coordinator,
            Stdout,
            Stderr,
            cancellationToken).ConfigureAwait(false);

        return ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        Dependencies.Dispose();

        if (_createdZShared && Directory.Exists(@"Z:\shared"))
        {
            try
            {
                Directory.Delete(@"Z:\shared", true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await TestContext.Out.WriteLineAsync($"Failed to delete 'Z:\\shared': {exception.Message}").ConfigureAwait(false);
            }
        }

        if (_substMapped)
        {
            try
            {
                var psi = new ProcessStartInfo("subst", "Z: /d")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    using var cts = new CancellationTokenSource(2000);
                    await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or OperationCanceledException
                or System.ComponentModel.Win32Exception)
            {
                await TestContext.Out.WriteLineAsync($"Failed to unmap 'Z:' subst drive: {exception.Message}").ConfigureAwait(false);
            }
        }

        TestUtilities.ResetStaticCaches();
        TestUtilities.ClearLocalFilesRedirect(TempHome);
    }
}
