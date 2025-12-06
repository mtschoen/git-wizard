using System.Runtime.InteropServices;

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
    /// Get all sub-directory paths within <paramref name="rootPath"/> that contain .git folders we care about.
    /// </summary>
    /// <param name="rootPath">The path to search.</param>
    /// <param name="paths">Collection of strings to store the results.</param>
    /// <param name="ignoredPaths">Collection of strings containing paths to ignore.</param>
    /// <param name="updateHandler">Optional handler for UI updates.</param>
    public static void GetRepositoryPaths(string rootPath, ICollection<string> paths,
        ICollection<string> ignoredPaths, IUpdateHandler? updateHandler = null)
    {
        // TODO: Find utility for interpreting special paths like %USERPROFILE% and ~
        // If the string starts with a % we can try treating it as an environment variable
        if (rootPath.StartsWith("%"))
        {
            var path = Environment.ExpandEnvironmentVariables(rootPath);
            if (!string.IsNullOrEmpty(path))
                rootPath = path;
        }

        if (rootPath == "~")
            rootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!Directory.Exists(rootPath))
        {
            GitWizardLog.Log($"Could not get repository paths at {rootPath} because it is not a directory", GitWizardLog.LogType.Error);
            return;
        }

        FindGitRepositoriesRecursively(rootPath, paths, ignoredPaths, updateHandler);

        try
        {
            updateHandler?.SendUpdateMessage($"Found {paths.Count} repositories");
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception thrown by GenerateReport onUpdate callback.");
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
                var split = subDirectory.Split(Path.DirectorySeparatorChar);
                var lastDirectory = split.Last();

                if (lastDirectory == ".git")
                {
                    lock (paths)
                    {
                        paths.Add(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                            ? rootPath.ToLowerInvariant()
                            : rootPath);
                    }

                    return;
                }

                // TODO: Add config setting for ignoring hidden directories
                if (split.Last().StartsWith("."))
                    return;

                var directoryInfo = new DirectoryInfo(subDirectory);
                if (directoryInfo.Attributes.HasFlag(FileAttributes.Hidden))
                    return;

                if (ignoredPaths.Contains(subDirectory))
                    return;

                FindGitRepositoriesRecursively(subDirectory, paths, ignoredPaths, updateHandler);
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

        return paths.Split('\n');
    }

    public static void SaveCachedRepositoryPaths(IEnumerable<string> paths)
    {
        EnsureLocalFolderExists();
        var path = GetCachedRepositoryListPath();
        File.WriteAllTextAsync(path, string.Join('\n', paths));
    }
}
