namespace GitWizard;

public static class GitWizardApi
{
    const string GitWizardFolder = ".GitWizard";
    const string RepositoryPathListFileName = "repositories";

    /// <summary>
    /// Set SilentMode to true to disable all console logging
    /// </summary>
    public static bool SilentMode { get; set; }

    public static string GetCachePath()
    {
        var homeFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeFolder, GitWizardFolder);
    }

    /// <summary>
    /// Get the path to the file where cached repository paths are stored.
    /// </summary>
    /// <returns>The path to the file where cached repository paths are stored.</returns>
    static string GetCachedRepositoryListPath()
    {
        return Path.Combine(GetCachePath(), RepositoryPathListFileName);
    }

    /// <summary>
    /// Get all sub-directory paths within <paramref name="rootPath"/> that contain .git folders we care about.
    /// </summary>
    /// <param name="rootPath">The path to search.</param>
    /// <param name="paths">Collection of strings to store the results.</param>
    /// <param name="ignoredPaths">Collection of strings containing paths to ignore.</param>
    /// <param name="onUpdate">Optional callback for progress updates.</param>
    public static async Task GetRepositoryPaths(string rootPath, ICollection<string> paths,
        ICollection<string> ignoredPaths, Action<string>? onUpdate = null)
    {
        // TODO: Find utility for interpreting special paths like %USERPROFILE% and ~
        // If the string starts with a % we can try treating it as an environment variable
        if (rootPath.StartsWith("%"))
        {
            var path = Environment.ExpandEnvironmentVariables(rootPath);
            if (string.IsNullOrEmpty(path))
                rootPath = path;
        }

        if (rootPath == "~")
            rootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!Directory.Exists(rootPath))
        {
            if (!SilentMode)
                Console.WriteLine($"Could not get repository paths at {rootPath} because it is not a directory");

            return;
        }

        await Task.Run(() => FindGitRepositoriesRecursively(rootPath, paths, ignoredPaths, onUpdate));
        onUpdate?.Invoke($"Found {paths.Count} repositories");
    }

    static async Task FindGitRepositoriesRecursively(string rootPath, ICollection<string> paths, ICollection<string> ignoredPaths, Action<string>? onUpdate = null)
    {
        try
        {
            onUpdate?.Invoke(rootPath);
            foreach (var subDirectory in Directory.GetDirectories(rootPath))
            {
                var split = subDirectory.Split(Path.DirectorySeparatorChar);
                var lastDirectory = split.Last();

                if (lastDirectory == ".git")
                {
                    lock (paths)
                    {
                        paths.Add(rootPath);
                    }

                    continue;
                }

                // TODO: Add config setting for ignoring hidden directories
                if (split.Last().StartsWith("."))
                    continue;

                var directoryInfo = new DirectoryInfo(subDirectory);
                if (directoryInfo.Attributes.HasFlag(FileAttributes.Hidden))
                    continue;

                if (ignoredPaths.Contains(subDirectory))
                    continue;

                await Task.Run(() => FindGitRepositoriesRecursively(subDirectory, paths, ignoredPaths, onUpdate));
            }
        }
        catch (Exception e)
        {
            onUpdate?.Invoke($"Exception reading {rootPath}: {e.Message}");
            // Ignore exceptions like access failure
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
        return paths.Split('\n');
    }
}
