using System.Diagnostics;
using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests.TestSupport;

sealed class CliDiscoveryFixture : IAsyncDisposable
{
    readonly string _command;
    readonly string[] _extraArgs;
    string? _substDrive;
    TempDirectory? _substDirectory;

    public DiscoveryDependencyRecorder Dependencies { get; } = new();
    public IReadOnlyList<IReadOnlyList<string>> OpenedDriveSets => Dependencies.OpenedDriveSets;
    public StringWriter Stdout { get; } = new();
    public StringWriter Stderr { get; } = new();
    readonly LocalFilesSandbox _sandbox = new();
    public GitWizardConfiguration Configuration { get; }
    bool NoMft { get; }
    int ExitCode { get; set; }

    public CliDiscoveryFixture(string command, bool noMft = false, params string[] extraArgs)
        : this(command, noMft, null, extraArgs)
    {
    }

    internal CliDiscoveryFixture(
        string command,
        bool noMft,
        Func<string>? sharedDirectoryMapper,
        params string[] extraArgs)
    {
        _command = command;
        NoMft = noMft;
        _extraArgs = extraArgs;

        GitWizardLog.SilentMode = true;

        try
        {
            var nonNtfsPath = sharedDirectoryMapper is null ? MapSharedDirectory() : sharedDirectoryMapper();
            var cPath = Dependencies.IndexFixture.TreePath;
            Configuration = new GitWizardConfiguration
            {
                SearchPaths = { cPath, nonNtfsPath }
            };
        }
        catch
        {
            try
            {
                Dependencies.Dispose();
            }
            finally
            {
                _sandbox.Dispose();
            }
            throw;
        }
    }

    string MapSharedDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return @"Z:\shared";
        }

        var drive = FindFreeDrive();
        _substDirectory = new TempDirectory("gw-subst-");
        try
        {
            Directory.CreateDirectory(Path.Combine(_substDirectory.Path, "shared"));
            var startInfo = new ProcessStartInfo("subst", $"{drive} \"{_substDirectory.Path}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException($"Could not start subst to map '{drive}'.");
            }

            if (!process.WaitForExit((int)TestTimeouts.HangGuard.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException($"Timed out mapping '{drive}' with subst.");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Mapping '{drive}' with subst failed with exit code {process.ExitCode}.");
            }

            _substDrive = drive;
            return $@"{drive}\shared";
        }
        catch
        {
            _substDirectory.Dispose();
            _substDirectory = null;
            throw;
        }
    }

    static string FindFreeDrive()
    {
        var occupiedDrives = DriveInfo.GetDrives()
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();
        for (var letter = 'Z'; letter >= 'A'; letter--)
        {
            if (!occupiedDrives.Contains(letter))
            {
                return $"{letter}:";
            }
        }

        throw new InvalidOperationException("CliDiscoveryFixture could not find a free drive letter from Z: through A: for its subst mapping.");
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

        var parseResult = CliParser.Parse([.. argsList]);

        var coordinator = new RepositoryDiscoveryCoordinator(Dependencies.BuildDependencies());
        ExitCode = await Program.RunCommandCoreAsync(
            parseResult,
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

        if (_substDrive is not null)
        {
            try
            {
                var startInfo = new ProcessStartInfo("subst", $"{_substDrive} /d")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    throw new InvalidOperationException("Could not start subst to remove the mapping.");
                }

                using var timeout = new CancellationTokenSource(TestTimeouts.HangGuard);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    await TestContext.Out.WriteLineAsync(
                        $"Unmapping '{_substDrive}' subst drive failed with exit code {process.ExitCode}.").ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or OperationCanceledException
                or System.ComponentModel.Win32Exception)
            {
                await TestContext.Out.WriteLineAsync($"Failed to unmap '{_substDrive}' subst drive: {exception.Message}").ConfigureAwait(false);
            }
        }

        _substDirectory?.Dispose();
        _sandbox.Dispose();
    }
}
