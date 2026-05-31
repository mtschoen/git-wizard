using System.Runtime.InteropServices;

namespace GitWizard;

public static partial class GitWizardApi
{
    const string GitWizardFolder = ".GitWizard";
    const string RepositoryPathListFileName = "repositories.txt";
    const string LogFolder = "Logs";

    // Reused line separators for splitting the cached repository-path file
    // (CA1861: avoid allocating the array on every call).
    static readonly string[] LineSeparators = ["\r\n", "\n"];

    public static string GetLocalFilesPath()
    {
        // A non-empty GITWIZARD_HOME env var redirects the data dir. Tests use it to isolate
        // off the real ~/.GitWizard (see TestUtilities.RedirectLocalFilesToTemp); it also makes
        // the config relocatable for any user who wants it elsewhere.
        var overridePath = Environment.GetEnvironmentVariable("GITWIZARD_HOME");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;

        var homeFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeFolder, GitWizardFolder);
    }

    public static string GetLogFolderPath()
    {
        return Path.Combine(GetLocalFilesPath(), LogFolder);
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
        return Path.Combine(GetLocalFilesPath(), RepositoryPathListFileName);
    }

    /// <summary>
    /// Expand a search path, resolving environment variables and ~.
    /// </summary>
    public static string? ExpandSearchPath(string rootPath)
    {
        rootPath = Environment.ExpandEnvironmentVariables(rootPath);

        if (rootPath.StartsWith("~/", StringComparison.Ordinal))
            rootPath = string.Concat(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), rootPath.AsSpan(1));

        if (!Directory.Exists(rootPath))
            return null;

        return NormalizePath(rootPath);
    }

    /// <summary>
    /// Expand environment variables and ~ in a path for display purposes only.
    /// Unlike ExpandSearchPath, this does NOT validate that the directory exists.
    /// </summary>
    public static string PrettyPrintPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path.Substring(2));
        }

        return path;
    }

    static StringComparison PathComparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        // Trim trailing separators, but preserve drive root paths (e.g. "C:\")
        // because "C:" without a trailing slash is a relative path on Windows.
        var root = Path.GetPathRoot(fullPath);
        if (fullPath.Length > 1 && fullPath != root)
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? fullPath.ToLowerInvariant()
            : fullPath;
    }

    static bool IsDriveRoot(string normalizedPath)
    {
        // e.g. "c:" on Windows or "/" on Unix
        var root = Path.GetPathRoot(normalizedPath);
        return root != null && string.Equals(
            normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            PathComparison);
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
    public static async Task<string[]?> GetCachedRepositoryPathsAsync(CancellationToken cancellationToken = default)
    {
        var fileName = GetCachedRepositoryListPath();
        if (!File.Exists(fileName))
            return null;

        var paths = await File.ReadAllTextAsync(fileName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(paths))
            return null;

        return paths
            .Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Get the cached list of repository paths (synchronous)
    /// </summary>
    /// <returns>An array containing the cached paths</returns>
    public static string[]? GetCachedRepositoryPaths()
    {
        var fileName = GetCachedRepositoryListPath();
        if (!File.Exists(fileName))
            return null;

        var paths = File.ReadAllText(fileName);
        if (string.IsNullOrWhiteSpace(paths))
            return null;

        return paths
            .Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static Task SaveCachedRepositoryPathsAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        EnsureLocalFolderExists();
        var path = GetCachedRepositoryListPath();
        var content = string.Join(Environment.NewLine, paths);
        return File.WriteAllTextAsync(path, content, cancellationToken);
    }

    public static void SaveCachedRepositoryPaths(IEnumerable<string> paths)
    {
        EnsureLocalFolderExists();
        var path = GetCachedRepositoryListPath();
        File.WriteAllText(path, string.Join(Environment.NewLine, paths));
    }

    /// <summary>
    /// Get the total size in bytes of the GitWizard local files folder (~/.GitWizard/).
    /// Returns 0 if the folder does not exist. Includes hidden and system files.
    /// </summary>
    public static long GetLocalFilesSize()
    {
        var path = GetLocalFilesPath();
        if (!Directory.Exists(path))
            return 0;

        return GetDirectorySize(path);
    }

    /// <summary>
    /// Recursively calculate total size of all files in <paramref name="path"/>.
    /// Includes hidden and system files. Skips directories/files that cause access or I/O errors.
    /// </summary>
    private static long GetDirectorySize(string path)
    {
        long totalSize = 0;

        try
        {
            var dirInfo = new DirectoryInfo(path);
            foreach (var file in dirInfo.GetFiles())
            {
                totalSize += file.Length;
            }

            foreach (var subDir in dirInfo.GetDirectories())
            {
                totalSize += GetDirectorySize(subDir.FullName);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            // Directory or file access denied: skip it and continue counting.
            GitWizardLog.Log($"GetDirectorySize: skipping inaccessible path: {exception.Message}", GitWizardLog.LogType.Verbose);
        }
        catch (IOException exception)
        {
            // I/O error reading directory: skip it and continue counting.
            GitWizardLog.Log($"GetDirectorySize: I/O error, skipping: {exception.Message}", GitWizardLog.LogType.Verbose);
        }

        return totalSize;
    }
}
