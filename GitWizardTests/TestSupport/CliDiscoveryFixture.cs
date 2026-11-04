using System.Diagnostics;
using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests.TestSupport;

sealed class CliDiscoveryFixture : IAsyncDisposable
{
    const string SubstMutexName = @"Global\GitWizardTests.SubstDrive";

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
            _substDirectory = new TempDirectory("gw-shared-");
            var shared = Path.Combine(_substDirectory.Path, "shared");
            Directory.CreateDirectory(shared);
            return shared;
        }

        _substDirectory = new TempDirectory("gw-subst-");
        try
        {
            Directory.CreateDirectory(Path.Combine(_substDirectory.Path, "shared"));
            _substDrive = MapSubstDrive(_substDirectory.Path);
            return $@"{_substDrive}\shared";
        }
        catch
        {
            _substDirectory.Dispose();
            _substDirectory = null;
            throw;
        }
    }

    // subst mappings are shared per Windows user session, while DriveInfo.GetDrives() and subst
    // are not atomic across processes. Serialize subst invocations under a machine-wide mutex so
    // two concurrent subst processes cannot both pass QueryDosDeviceW for the same letter, and on
    // nonzero exit re-enumerate occupied drives to retry the next unattempted letter without
    // unmapping a drive claimed by a competing test process.
    internal static string MapSubstDrive(string backingDirectory, Func<DriveInfo[]>? getOccupiedDrives = null)
    {
        var enumerateDrives = getOccupiedDrives ?? DriveInfo.GetDrives;
        var attemptedLetters = new HashSet<char>();
        var attemptedDrives = new List<string>();
        var lastExitCode = 0;

        while (SelectCandidateLetter(enumerateDrives, attemptedLetters) is { } letter)
        {
            attemptedLetters.Add(letter);
            var drive = $"{letter}:";
            attemptedDrives.Add(drive);

            var exitCode = RunSubstWithMutex($"{drive} \"{backingDirectory}\"", $"mapping '{drive}'");
            if (exitCode == 0)
            {
                return drive;
            }

            lastExitCode = exitCode;
        }

        if (attemptedDrives.Count == 0)
        {
            throw new InvalidOperationException(
                "CliDiscoveryFixture could not find a free drive letter from Z: through A: for its subst mapping.");
        }

        throw new InvalidOperationException(
            $"Mapping subst drive failed after attempting [{string.Join(", ", attemptedDrives)}]; last attempt '{attemptedDrives[^1]}' exited with code {lastExitCode}.");
    }

    static char? SelectCandidateLetter(Func<DriveInfo[]> enumerateDrives, HashSet<char> attemptedLetters)
    {
        var occupiedDrives = enumerateDrives()
            .Where(drive => !string.IsNullOrEmpty(drive.Name))
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();
        for (var letter = 'Z'; letter >= 'A'; letter--)
        {
            if (!occupiedDrives.Contains(letter) && !attemptedLetters.Contains(letter))
            {
                return letter;
            }
        }

        return null;
    }

    static int RunSubstWithMutex(string arguments, string operationDescription)
    {
        using var mutex = new Mutex(initiallyOwned: false, SubstMutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TestTimeouts.HangGuard);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException($"Timed out waiting for subst mutex while {operationDescription}.");
            }

            var startInfo = new ProcessStartInfo("subst", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException($"Could not start subst while {operationDescription}.");
            }

            if (!process.WaitForExit((int)TestTimeouts.HangGuard.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException($"Timed out while {operationDescription} with subst.");
            }

            return process.ExitCode;
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
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
            await UnmapSubstDriveAsync(_substDrive).ConfigureAwait(false);
        }

        _substDirectory?.Dispose();
        _sandbox.Dispose();
    }

    internal static async ValueTask UnmapSubstDriveAsync(string drive)
    {
        try
        {
            var exitCode = RunSubstWithMutex($"{drive} /d", $"unmapping '{drive}'");
            if (exitCode != 0)
            {
                await TestContext.Out.WriteLineAsync(
                    $"Unmapping '{drive}' subst drive failed with exit code {exitCode}.").ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            await TestContext.Out.WriteLineAsync($"Failed to unmap '{drive}' subst drive: {exception.Message}").ConfigureAwait(false);
        }
    }
}
