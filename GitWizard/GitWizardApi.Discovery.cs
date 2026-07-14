using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib;

namespace GitWizard;

public static partial class GitWizardApi
{
    /// <summary>
    /// Try to find all git repositories across all search paths using MFT, taking the cold
    /// scan through MFTLib's elevated journal broker when not already elevated - one UAC
    /// prompt for the whole run, no temp-file handoff. Repository roots are pulled from the
    /// broker's <see cref="ScanRecord"/>s exactly as the already-elevated in-process scan
    /// pulls them from <see cref="MftVolume"/>.
    /// </summary>
    /// <param name="configuration">Search and ignored paths to scan.</param>
    /// <param name="paths">Collection that discovered repository roots are added to.</param>
    /// <param name="updateHandler">Optional handler for UI progress updates.</param>
    /// <param name="noMft">When true, skip MFT discovery entirely and return false.</param>
    /// <param name="elevation">Elevation provider; defaults to the real one. Injected in tests.</param>
    /// <param name="scanProvider">
    /// Seam that returns the cold-scan records for the given drive letters. Defaults to
    /// spawning a real <see cref="JournalBrokerClient"/>; injected in tests to exercise the
    /// filtering flow without real elevation.
    /// </param>
    /// <returns>True if MFT search was used successfully.</returns>
    public static async Task<bool> TryFindAllRepositoriesUsingMftAsync(
        GitWizardConfiguration configuration, ICollection<string> paths,
        IUpdateHandler? updateHandler = null, bool noMft = false, IElevationProvider? elevation = null,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<ScanRecord>>>? scanProvider = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        if (noMft)
            return false;

        elevation ??= ElevationUtilities.DefaultProvider;

        if (elevation.IsElevated())
        {
            // Already elevated - scan directly, no child process needed.
            foreach (var searchPath in configuration.SearchPaths)
            {
                var expanded = ExpandSearchPath(searchPath);
                if (expanded == null)
                    continue;

                FindGitRepositoriesUsingMft(expanded, paths, configuration.IgnoredPaths, updateHandler,
                    configuration.SkipHiddenDirectories);
            }

            return paths.Count > 0;
        }

        // Not elevated - take the cold scan through the journal broker (single UAC prompt).
        var expandedRoots = configuration.SearchPaths
            .Select(ExpandSearchPath)
            .OfType<string>()
            .ToList();
        if (expandedRoots.Count == 0)
            return false;

        var drives = expandedRoots
            .Select(GetDriveLetter)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (drives.Count == 0)
            return false;

        scanProvider ??= BrokerScanAsync;

        IReadOnlyList<ScanRecord> records;
        try
        {
            records = await scanProvider(drives).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            // Broker spawn declined (UAC) or failed - let the caller fall back to a directory scan.
            GitWizardLog.Log($"MFT broker scan unavailable: {exception.Message}", GitWizardLog.LogType.Warning);
            return false;
        }

        foreach (var searchPath in expandedRoots)
            CollectGitRepositoriesFromScan(records, searchPath, configuration.IgnoredPaths, paths,
                configuration.SkipHiddenDirectories);

        updateHandler?.SendUpdateMessage($"MFT search found {paths.Count} repositories");
        return paths.Count > 0;
    }

    // Spawns the real elevated broker, takes one cold scan across the given drives, and
    // returns its records. Throws InvalidOperationException if the broker can't be started.
    [SupportedOSPlatform("windows")]
    static async Task<IReadOnlyList<ScanRecord>> BrokerScanAsync(IReadOnlyList<string> drives)
    {
        var client = await JournalBrokerClient
            .SpawnAndConnectAsync(BrokerLauncher.Launch, CancellationToken.None).ConfigureAwait(false);

        await using (client.ConfigureAwait(false))
        {
            var scan = await client.ArmScanAndCatchUpAsync(drives, CancellationToken.None).ConfigureAwait(false);
            return scan.Records;
        }
    }

    // Filters one drive's cold-scan records down to the repository roots under a single
    // search path, applying the same drive-root rule as the in-process scan: at a drive root
    // only .git directories count; under a scoped path, .git files (worktree/submodule
    // pointers) count too.
    static void CollectGitRepositoriesFromScan(IReadOnlyList<ScanRecord> records, string rootPath,
        ICollection<string> ignoredPaths, ICollection<string> paths, bool? skipHiddenDirectories)
    {
        var includeGitFiles = !IsDriveRoot(NormalizePath(rootPath));
        var gitEntryPaths = SelectGitEntryPaths(records, includeGitFiles);
        CollectGitRepositories(gitEntryPaths, rootPath, ignoredPaths, paths, skipHiddenDirectories);
    }

    // The .git-named scan records, as their paths. Pure: no filesystem access, so the
    // drive-root include-files rule is unit-testable with synthetic records.
    static IEnumerable<string> SelectGitEntryPaths(IReadOnlyList<ScanRecord> records, bool includeGitFiles) =>
        records
            .Where(record => record.Name == ".git" && (includeGitFiles || record.IsDirectory))
            .Select(record => record.Path);

    static string? GetDriveLetter(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? null : root[..1];
    }

    /// <summary>
    /// Get all sub-directory paths within <paramref name="rootPath"/> that contain .git folders we care about.
    /// </summary>
    /// <param name="rootPath">The path to search.</param>
    /// <param name="paths">Collection of strings to store the results.</param>
    /// <param name="ignoredPaths">Collection of strings containing paths to ignore.</param>
    /// <param name="updateHandler">Optional handler for UI updates.</param>
    /// <param name="skipHiddenDirectories">Whether to skip directories whose last path segment starts with '.'.
    /// Null uses the default behavior (skip hidden directories).</param>
    public static void GetRepositoryPaths(string rootPath, ICollection<string> paths,
        ICollection<string> ignoredPaths, IUpdateHandler? updateHandler = null,
        bool? skipHiddenDirectories = null)
    {
        var expanded = ExpandSearchPath(rootPath);
        if (expanded == null)
        {
            GitWizardLog.Log($"Could not get repository paths at {rootPath} because it is not a directory", GitWizardLog.LogType.Error);
            return;
        }

        rootPath = expanded;

        // When searching a full drive, skip .git files - submodules/worktrees are found during refresh.
        // When searching a scoped path, include .git files for worktrees/submodules whose parent
        // may be outside the search scope.
        var includeGitFiles = !IsDriveRoot(rootPath);
        FindGitRepositoriesRecursively(rootPath, paths, ExpandIgnoredPaths(ignoredPaths), includeGitFiles, updateHandler, skipHiddenDirectories);

        try
        {
            updateHandler?.SendUpdateMessage($"Found {paths.Count} repositories");
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception thrown by GenerateReport onUpdate callback.");
        }
    }

    static void FindGitRepositoriesUsingMft(string rootPath, ICollection<string> paths,
        ICollection<string> ignoredPaths, IUpdateHandler? updateHandler = null,
        bool? skipHiddenDirectories = null)
    {
        try
        {
            var driveLetter = GetDriveLetter(rootPath);
            if (driveLetter == null)
                return;

            updateHandler?.SendUpdateMessage($"Using MFT to search {driveLetter}: drive");

            using var volume = MftVolume.Open(driveLetter);

            // When searching a full drive, only find .git directories - submodules and worktrees
            // (.git files) will be discovered during refresh of the parent repo.
            // When searching a scoped path, also find .git files in case the search root itself
            // is a worktree or submodule whose parent is outside the search scope.
            var gitEntries = IsDriveRoot(NormalizePath(rootPath))
                ? volume.FindDirectories(".git")
                : volume.FindRecords(".git");

            CollectGitRepositories(gitEntries, rootPath, ignoredPaths, paths, skipHiddenDirectories);

            updateHandler?.SendUpdateMessage($"MFT search found {paths.Count} repositories on {driveLetter}:");
        }
        catch (Exception exception)
        {
            GitWizardLog.Log($"MFT search failed, falling back to directory scan: {exception.Message}",
                GitWizardLog.LogType.Warning);
        }
    }

    // Turns a set of .git entry paths (from a live MFT scan or a broker cold scan) into the
    // repository roots under rootPath: the .git entry's parent must be under the search root,
    // not ignored, and (unless skipHiddenDirectories is false) free of hidden/dot path
    // segments, and must still exist on disk.
    static void CollectGitRepositories(IEnumerable<string> gitEntryPaths, string rootPath,
        ICollection<string> ignoredPaths, ICollection<string> paths, bool? skipHiddenDirectories = null)
    {
        var normalizedRootPath = NormalizePath(rootPath);
        var expandedIgnoredPaths = ExpandIgnoredPaths(ignoredPaths);
        var shouldSkipHiddenDirs = skipHiddenDirectories != false;
        var candidates = new List<string>();

        foreach (var gitEntryPath in gitEntryPaths)
        {
            var parentPath = Path.GetDirectoryName(gitEntryPath);
            if (parentPath == null)
                continue;

            var normalizedParentPath = NormalizePath(parentPath);

            // Must be under the root search path
            if (!IsSameOrChildPath(normalizedParentPath, normalizedRootPath))
                continue;

            // Skip ignored paths
            if (expandedIgnoredPaths.Any(ignored => IsSameOrChildPath(normalizedParentPath, ignored)))
                continue;

            // Skip paths containing hidden/dot directories (matches recursive search behavior)
            if (shouldSkipHiddenDirs)
            {
                var relativePath = normalizedParentPath[normalizedRootPath.Length..];
                if (relativePath.Split(Path.DirectorySeparatorChar)
                    .Any(segment => segment.Length > 0 && segment.StartsWith('.')))
                    continue;
            }

            // Verify the directory is accessible
            if (!Directory.Exists(parentPath))
                continue;

            candidates.Add(normalizedParentPath);
        }

        foreach (var candidate in candidates)
        {
            lock (paths)
            {
                paths.Add(candidate);
            }
        }
    }

    static void FindGitRepositoriesRecursively(string rootPath, ICollection<string> paths, ICollection<string> ignoredPaths, bool includeGitFiles, IUpdateHandler? updateHandler = null, bool? skipHiddenDirectories = null)
    {
        try
        {
            try
            {
                updateHandler?.SendUpdateMessage(rootPath);
            }
            catch (Exception nextException)
            {
                GitWizardLog.LogException(nextException, "Exception thrown by GenerateReport onUpdate callback.");
            }

            // Check for .git file (submodule/worktree pointer) in current directory
            if (includeGitFiles && File.Exists(Path.Combine(rootPath, ".git")))
            {
                lock (paths)
                {
                    paths.Add(NormalizePath(rootPath));
                }
            }

            string[]? directories;
            try
            {
                directories = Directory.GetDirectories(rootPath);
                if (directories.Length == 0)
                    return;
            }
            catch
            {
                return;
            }

            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            Parallel.ForEach(directories, parallelOptions, subDirectory =>
            {
                try
                {
                    var normalizedSubDirectory = NormalizePath(subDirectory);
                    var split = subDirectory.Split(Path.DirectorySeparatorChar);
                    var lastDirectory = split.Last();

                    if (lastDirectory == ".git")
                    {
                        lock (paths)
                        {
                            paths.Add(NormalizePath(rootPath));
                        }

                        return; // Ends only this lambda invocation, not the enclosing method - Parallel.ForEach has no `continue`.
                    }

                    if (split.Last().StartsWith('.') && skipHiddenDirectories != false)
                        return;

                    if (OperatingSystem.IsWindows())
                    {
                        var directoryInfo = new DirectoryInfo(subDirectory);
                        if (directoryInfo.Attributes.HasFlag(FileAttributes.Hidden))
                            return;
                    }

                    if (ignoredPaths.Any(ignored => IsSameOrChildPath(normalizedSubDirectory, ignored)))
                        return;

                    FindGitRepositoriesRecursively(normalizedSubDirectory, paths, ignoredPaths, includeGitFiles, updateHandler, skipHiddenDirectories);
                }
                catch (Exception exception)
                {
                    // Ignore per-directory exceptions (access denied, etc.) and continue the walk.
                    GitWizardLog.Log($"Skipping directory during scan: {exception.Message}", GitWizardLog.LogType.Verbose);
                }
            });
        }
        catch (Exception exception)
        {
            // Ignore exceptions like access failure
            try
            {
                updateHandler?.SendUpdateMessage($"Exception reading {rootPath}: {exception.Message}");
            }
            catch (Exception nextException)
            {
                GitWizardLog.LogException(nextException, "Exception thrown by GenerateReport onUpdate callback.");
            }
        }
    }
}
