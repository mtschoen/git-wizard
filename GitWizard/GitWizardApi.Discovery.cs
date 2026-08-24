using MFTLib;

namespace GitWizard;

public static partial class GitWizardApi
{
    // Cold-scan profile and keep-file name BrokerScanWithSessionAsync passes to the session - internal so
    // BrokerDiscoveryTests's wire-level assertion can reuse them instead of repeating literals.
    internal const BrokerScanProfile GitDiscoveryScanProfile = BrokerScanProfile.DirectoryIndex;
    internal const string GitEntryName = ".git";

    // Filters one drive's cold-scan records down to the repository roots under a single
    // search path, applying the same drive-root rule as the in-process scan: at a drive root
    // only .git directories count; under a scoped path, .git files (worktree/submodule
    // pointers) count too.
    internal static void CollectGitRepositoriesFromScan(IReadOnlyList<ScanRecord> records, string rootPath,
        ICollection<string> ignoredPaths, ICollection<string> paths, bool? skipHiddenDirectories)
    {
        var includeGitFiles = !IsDriveRoot(NormalizePath(rootPath));
        var gitEntryPaths = SelectGitEntryPaths(records, includeGitFiles);
        CollectGitRepositories(gitEntryPaths, rootPath, ignoredPaths, paths, skipHiddenDirectories);
    }

    // The .git-named scan records, as their paths. Pure: no filesystem access, so the
    // drive-root include-files rule is unit-testable with synthetic records. A .git FILE
    // record (worktree/submodule pointer, IsDirectory: false) is kept whenever includeGitFiles
    // is true - its parent directory becomes a repository root just like a .git directory's
    // parent does (see ResolveRepositoryRootCandidates).
    internal static IEnumerable<string> SelectGitEntryPaths(IReadOnlyList<ScanRecord> records, bool includeGitFiles) =>
        records
            .Where(record => record.Name == GitEntryName && (includeGitFiles || record.IsDirectory))
            .Select(record => record.Path);

    // internal: also the canonical GetDriveLetter for GitWizard.Watch.UsnVolumeChangeSource
    // (purely Path.GetPathRoot-based, so sharing it doesn't broaden that Windows-gated type's
    // platform surface).
    internal static string? GetDriveLetter(string path)
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
        var context = new DiscoveryContext(paths, ExpandIgnoredPaths(ignoredPaths), includeGitFiles, updateHandler, skipHiddenDirectories);
        FindGitRepositoriesRecursively(rootPath, context);

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
    // segments, and must still exist on disk. A .git entry can be either a directory (a normal
    // repository) or a file (a worktree/submodule pointer, whose real gitdir may live outside
    // every search path) - both map to their parent directory as the repository root the same
    // way; the includeGitFiles flag upstream in SelectGitEntryPaths is what decides whether
    // .git files are offered to this method at all.
    static void CollectGitRepositories(IEnumerable<string> gitEntryPaths, string rootPath,
        ICollection<string> ignoredPaths, ICollection<string> paths, bool? skipHiddenDirectories = null)
    {
        var candidates = ResolveRepositoryRootCandidates(gitEntryPaths, rootPath, ignoredPaths, skipHiddenDirectories)
            .Where(candidate => Directory.Exists(candidate))
            .ToList();

        foreach (var candidate in candidates)
        {
            lock (paths)
            {
                paths.Add(candidate);
            }
        }
    }

    // Pure candidate-root derivation: maps each .git entry path to its parent directory and
    // applies root-scope, ignored-path, and hidden-directory filtering - everything
    // CollectGitRepositories does except the Directory.Exists disk check, so it can be
    // exercised with synthetic ScanRecord-derived paths (real or nonexistent) on any OS.
    internal static IEnumerable<string> ResolveRepositoryRootCandidates(IEnumerable<string> gitEntryPaths,
        string rootPath, ICollection<string> ignoredPaths, bool? skipHiddenDirectories = null)
    {
        var normalizedRootPath = NormalizePath(rootPath);
        var expandedIgnoredPaths = ExpandIgnoredPaths(ignoredPaths);
        var shouldSkipHiddenDirs = skipHiddenDirectories != false;

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

            yield return normalizedParentPath;
        }
    }

    readonly record struct DiscoveryContext(
        ICollection<string> Paths,
        ICollection<string> IgnoredPaths,
        bool IncludeGitFiles,
        IUpdateHandler? UpdateHandler,
        bool? SkipHiddenDirectories);

    static void FindGitRepositoriesRecursively(string rootPath, DiscoveryContext context)
    {
        try
        {
            try
            {
                context.UpdateHandler?.SendUpdateMessage(rootPath);
            }
            catch (Exception nextException)
            {
                GitWizardLog.LogException(nextException, "Exception thrown by GenerateReport onUpdate callback.");
            }

            if (IsBareGitRepository(rootPath))
            {
                lock (context.Paths)
                {
                    context.Paths.Add(NormalizePath(rootPath));
                }
                return;
            }

            if (context.IncludeGitFiles && File.Exists(Path.Combine(rootPath, ".git")))
            {
                lock (context.Paths)
                {
                    context.Paths.Add(NormalizePath(rootPath));
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
                ProcessSubDirectory(subDirectory, rootPath, context);
            });
        }
        catch (Exception exception)
        {
            // Ignore exceptions like access failure
            try
            {
                context.UpdateHandler?.SendUpdateMessage($"Exception reading {rootPath}: {exception.Message}");
            }
            catch (Exception nextException)
            {
                GitWizardLog.LogException(nextException, "Exception thrown by GenerateReport onUpdate callback.");
            }
        }
    }

    static void ProcessSubDirectory(
        string subDirectory,
        string rootPath,
        DiscoveryContext context)
    {
        try
        {
            var normalizedSubDirectory = NormalizePath(subDirectory);
            var split = subDirectory.Split(Path.DirectorySeparatorChar);
            var lastDirectory = split.Last();

            if (lastDirectory == ".git")
            {
                lock (context.Paths)
                {
                    context.Paths.Add(NormalizePath(rootPath));
                }

                return; // Ends only this lambda invocation, not the enclosing method - Parallel.ForEach has no `continue`.
            }

            if (split.Last().StartsWith('.') && context.SkipHiddenDirectories != false)
                return;

            if (OperatingSystem.IsWindows())
            {
                var directoryInfo = new DirectoryInfo(subDirectory);
                if (directoryInfo.Attributes.HasFlag(FileAttributes.Hidden))
                    return;
            }

            if (context.IgnoredPaths.Any(ignored => IsSameOrChildPath(normalizedSubDirectory, ignored)))
                return;

            if (IsBareGitRepository(subDirectory))
            {
                lock (context.Paths)
                {
                    context.Paths.Add(normalizedSubDirectory);
                }

                return;
            }

            FindGitRepositoriesRecursively(normalizedSubDirectory, context);
        }
        catch (Exception exception)
        {
            // Ignore per-directory exceptions (access denied, etc.) and continue the walk.
            GitWizardLog.Log($"Skipping directory during scan: {exception.Message}", GitWizardLog.LogType.Verbose);
        }
    }

    internal static bool IsBareGitRepository(string path)
    {
        return File.Exists(Path.Combine(path, "HEAD")) &&
               Directory.Exists(Path.Combine(path, "objects")) &&
               Directory.Exists(Path.Combine(path, "refs"));
    }
}

