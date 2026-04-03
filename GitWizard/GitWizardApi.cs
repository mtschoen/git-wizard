using System.Runtime.InteropServices;
using MFTLib;

namespace GitWizard;

public static class GitWizardApi
{
    const string k_GitWizardFolder = ".GitWizard";
    const string k_RepositoryPathListFileName = "repositories.txt";
    const string k_LogFolder = "Logs";

    public static string GetLocalFilesPath()
    {
        var homeFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeFolder, k_GitWizardFolder);
    }

    public static string GetLogFolderPath()
    {
        return Path.Combine(GetLocalFilesPath(), k_LogFolder);
    }

    public static void EnsureLocalFolderExists()
    {
        var path = GetLocalFilesPath();
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }

    /// <summary>
    /// Get the path to the file where cached repository paths are stored.
    /// </summary>
    /// <returns>The path to the file where cached repository paths are stored.</returns>
    public static string GetCachedRepositoryListPath()
    {
        return Path.Combine(GetLocalFilesPath(), k_RepositoryPathListFileName);
    }

    /// <summary>
    /// Expand a search path, resolving environment variables and ~.
    /// </summary>
    public static string? ExpandSearchPath(string rootPath)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(rootPath);
        if (!string.IsNullOrEmpty(expandedPath))
            rootPath = expandedPath;

        if (rootPath == "~")
            rootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!Directory.Exists(rootPath))
            return null;

        return NormalizePath(rootPath);
    }

    static StringComparison PathComparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length > 1)
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? fullPath.ToLowerInvariant()
            : fullPath;
    }

    static List<string> ExpandIgnoredPaths(IEnumerable<string> ignoredPaths)
    {
        return ignoredPaths
            .Select(ExpandSearchPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Cast<string>()
            .ToList();
    }

    static bool IsSameOrChildPath(string path, string rootPath)
    {
        if (string.Equals(path, rootPath, PathComparison))
            return true;

        if (!path.StartsWith(rootPath, PathComparison))
            return false;

        if (path.Length == rootPath.Length)
            return true;

        var nextChar = path[rootPath.Length];
        return nextChar == Path.DirectorySeparatorChar || nextChar == Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    /// Try to find all git repositories across all search paths using MFT.
    /// On Windows, this will launch an elevated helper process if not already elevated.
    /// </summary>
    /// <returns>True if MFT search was used successfully.</returns>
    public static bool TryFindAllRepositoriesUsingMft(GitWizardConfiguration configuration,
        ICollection<string> paths, IUpdateHandler? updateHandler = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        if (Environment.GetEnvironmentVariable("GITWIZARD_NO_MFT") == "1")
            return false;

        if (ElevationUtilities.IsElevated())
        {
            // Already elevated — scan directly
            foreach (var searchPath in configuration.SearchPaths)
            {
                var expanded = ExpandSearchPath(searchPath);
                if (expanded == null)
                    continue;

                TryFindGitRepositoriesUsingMft(expanded, paths, configuration.IgnoredPaths, updateHandler);
            }

            return paths.Count > 0;
        }

        // Not elevated — try launching an elevated helper
        var configPath = GitWizardConfiguration.GetGlobalConfigurationPath();
        var outputPath = Path.Combine(Path.GetTempPath(), $"gitwizard-mft-{Guid.NewGuid()}.txt");

        try
        {
            if (!ElevationUtilities.TryRunElevated($"--elevated-mft --config-path \"{configPath}\" --output \"{outputPath}\"", timeoutMs: 120000))
                return false;

            if (!File.Exists(outputPath))
                return false;

            var lines = File.ReadAllLines(outputPath);
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    paths.Add(line);
            }

            updateHandler?.SendUpdateMessage($"MFT search found {paths.Count} repositories");
            return paths.Count > 0;
        }
        finally
        {
            try { File.Delete(outputPath); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Run the elevated MFT scan and write results to the output file.
    /// Called by the elevated child process.
    /// </summary>
    public static void RunElevatedMftScan(string configPath, string outputPath)
    {
        var configuration = GitWizardConfiguration.GetConfigurationAtPath(configPath)
                            ?? GitWizardConfiguration.CreateDefaultConfiguration();

        var paths = new SortedSet<string>();

        foreach (var searchPath in configuration.SearchPaths)
        {
            var expanded = ExpandSearchPath(searchPath);
            if (expanded == null)
                continue;

            TryFindGitRepositoriesUsingMft(expanded, paths, configuration.IgnoredPaths);
        }

        File.WriteAllLines(outputPath, paths);
    }

    /// <summary>
    /// Get all sub-directory paths within <paramref name="rootPath"/> that contain .git folders we care about.
    /// </summary>
    /// <param name="rootPath">The path to search.</param>
    /// <param name="paths">Collection of strings to store the results.</param>
    /// <param name="ignoredPaths">Collection of strings containing paths to ignore.</param>
    /// <param name="updateHandler">Optional handler for UI updates.</param>
    public static void GetRepositoryPaths(string rootPath, ICollection<string> paths,
        ICollection<string> ignoredPaths, IUpdateHandler? updateHandler = null)
    {
        var expanded = ExpandSearchPath(rootPath);
        if (expanded == null)
        {
            GitWizardLog.Log($"Could not get repository paths at {rootPath} because it is not a directory", GitWizardLog.LogType.Error);
            return;
        }

        rootPath = expanded;

        FindGitRepositoriesRecursively(rootPath, paths, ExpandIgnoredPaths(ignoredPaths), updateHandler);

        try
        {
            updateHandler?.SendUpdateMessage($"Found {paths.Count} repositories");
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception thrown by GenerateReport onUpdate callback.");
        }
    }

    static bool TryFindGitRepositoriesUsingMft(string rootPath, ICollection<string> paths,
        ICollection<string> ignoredPaths, IUpdateHandler? updateHandler = null)
    {
        try
        {
            var driveLetter = Path.GetPathRoot(rootPath)?[..1];
            if (driveLetter == null)
                return false;

            updateHandler?.SendUpdateMessage($"Using MFT to search {driveLetter}: drive");

            using var volume = MftVolume.Open(driveLetter);
            var normalizedRootPath = NormalizePath(rootPath);
            var expandedIgnoredPaths = ExpandIgnoredPaths(ignoredPaths);

            // Collect candidates first, then filter out nested repos
            var candidates = new List<string>();

            foreach (var gitDirPath in volume.FindRecords(".git"))
            {
                var parentPath = Path.GetDirectoryName(gitDirPath);
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
                var relativePath = normalizedParentPath[normalizedRootPath.Length..];
                if (relativePath.Split(Path.DirectorySeparatorChar)
                    .Any(segment => segment.Length > 0 && segment.StartsWith('.')))
                    continue;

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

            updateHandler?.SendUpdateMessage($"MFT search found {paths.Count} repositories on {driveLetter}:");

            return true;
        }
        catch (Exception exception)
        {
            GitWizardLog.Log($"MFT search failed, falling back to directory scan: {exception.Message}",
                GitWizardLog.LogType.Warning);
            return false;
        }
    }

    static void FindGitRepositoriesRecursively(string rootPath, ICollection<string> paths, ICollection<string> ignoredPaths, IUpdateHandler? updateHandler = null)
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
            if (File.Exists(Path.Combine(rootPath, ".git")))
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

            Parallel.ForEach(directories, subDirectory =>
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

                        return;
                    }

                    // TODO: Add config setting for ignoring hidden directories
                    if (split.Last().StartsWith("."))
                        return;

                    var directoryInfo = new DirectoryInfo(subDirectory);
                    if (directoryInfo.Attributes.HasFlag(FileAttributes.Hidden))
                        return;

                    if (ignoredPaths.Any(ignored => IsSameOrChildPath(normalizedSubDirectory, ignored)))
                        return;

                    FindGitRepositoriesRecursively(normalizedSubDirectory, paths, ignoredPaths, updateHandler);
                }
                catch
                {
                    // Ignore per-directory exceptions (access denied, etc.)
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

    public static void ClearCache()
    {
        try
        {
            var cachedRepositoryListPath = GetCachedRepositoryListPath();
            if (File.Exists(cachedRepositoryListPath))
                File.Delete(cachedRepositoryListPath);

            var cachedReportPath = GitWizardReport.GetCachedReportPath();
            if (File.Exists(cachedReportPath))
                File.Delete(cachedReportPath);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Caught exception trying to clear cache");
        }
    }

    public static void DeleteAllLocalFiles()
    {
        try
        {
            GitWizardLog.CloseCurrentLogFile();
            Directory.Delete(GetLocalFilesPath(), true);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Caught exception trying to delete all local files");
        }
    }

    /// <summary>
    /// Get the cached list of repository paths
    /// </summary>
    /// <returns>An array containing the cached paths</returns>
    public static string[]? GetCachedRepositoryPaths()
    {
        var fileName = GetCachedRepositoryListPath();
        if (!File.Exists(fileName))
            return null;

        // TODO: Async file reads
        var paths = File.ReadAllText(fileName);
        if (string.IsNullOrWhiteSpace(paths))
            return null;

        return paths
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static void SaveCachedRepositoryPaths(IEnumerable<string> paths)
    {
        EnsureLocalFolderExists();
        var path = GetCachedRepositoryListPath();
        File.WriteAllText(path, string.Join(Environment.NewLine, paths));
    }
}
