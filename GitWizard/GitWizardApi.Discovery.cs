using MFTLib;

namespace GitWizard;

public static partial class GitWizardApi
{
    internal const BrokerScanProfile GitDiscoveryScanProfile = BrokerScanProfile.DirectoryIndex;
    internal const string GitEntryName = ".git";

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
        bool? skipHiddenDirectories = null) =>
        GetRepositoryPaths(rootPath, paths, ignoredPaths, updateHandler, skipHiddenDirectories, includeTempDirectories: null);

    /// <summary>
    /// Get all sub-directory paths within <paramref name="rootPath"/> that contain .git folders we care about.
    /// </summary>
    /// <param name="rootPath">The path to search.</param>
    /// <param name="paths">Collection of strings to store the results.</param>
    /// <param name="ignoredPaths">Collection of strings containing paths to ignore.</param>
    /// <param name="updateHandler">Optional handler for UI updates.</param>
    /// <param name="skipHiddenDirectories">Whether to skip directories whose last path segment starts with '.'.
    /// Null uses the default behavior (skip hidden directories).</param>
    /// <param name="includeTempDirectories">Whether to include temp directories (any profile's
    /// AppData\Local\Temp, the process temp roots, /tmp) as repository locations. Null or false
    /// excludes them, unless the search root itself is inside a temp directory.</param>
    public static void GetRepositoryPaths(string rootPath, ICollection<string> paths,
        ICollection<string> ignoredPaths, IUpdateHandler? updateHandler,
        bool? skipHiddenDirectories, bool? includeTempDirectories)
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
        // A search root inside a temp directory opts that subtree back in: explicit scope beats
        // the default temp exclusion (and the test suite's fixtures all live under temp).
        var excludeTempDirectories = includeTempDirectories != true && !TempDirectories.IsTempPath(rootPath);
        var context = new DiscoveryContext(paths, ExpandIgnoredPaths(ignoredPaths), includeGitFiles, updateHandler,
            skipHiddenDirectories, excludeTempDirectories);
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

    readonly record struct DiscoveryContext(
        ICollection<string> Paths,
        ICollection<string> IgnoredPaths,
        bool IncludeGitFiles,
        IUpdateHandler? UpdateHandler,
        bool? SkipHiddenDirectories,
        bool ExcludeTempDirectories);

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

            if (context.ExcludeTempDirectories && TempDirectories.IsTempPath(normalizedSubDirectory))
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

