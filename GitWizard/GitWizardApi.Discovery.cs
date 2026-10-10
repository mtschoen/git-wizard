using System.Security;
using GitWizard.Watch;

namespace GitWizard;

public static partial class GitWizardApi
{
    internal const string GitEntryName = ".git";

    internal static Func<string, string[]>? DirectoryEnumeratorOverride { get; set; }

    /// <summary>
    /// Discovers repositories under <paramref name="expandedRoot"/> recursively.
    /// </summary>
    public static RecursiveDiscovery DiscoverRecursively(
        DiscoveryRequest request, string expandedRoot, CancellationToken cancellationToken)
    {
        return DiscoverRecursively(request, expandedRoot, null, cancellationToken);
    }

    internal static RecursiveDiscovery DiscoverRecursively(
        DiscoveryRequest request, string expandedRoot, IUpdateHandler? updateHandler,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var paths = new List<string>();
        var failedSubtrees = new List<string>();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(expandedRoot))
        {
            errors[expandedRoot] = $"Directory does not exist: {expandedRoot}";
            failedSubtrees.Add(expandedRoot);
            return new RecursiveDiscovery(paths, failedSubtrees, errors);
        }

        var includeGitFiles = !IsDriveRoot(expandedRoot);
        var excludeTempDirectories = request.Configuration.IncludeTempDirectories != true &&
            !TempDirectories.IsTempPath(expandedRoot);

        var context = new DiscoveryContext(
            paths,
            failedSubtrees,
            errors,
            ExpandIgnoredPaths(request.Configuration.IgnoredPaths),
            includeGitFiles,
            updateHandler,
            request.Configuration.SkipHiddenDirectories,
            excludeTempDirectories,
            cancellationToken);

        FindGitRepositoriesRecursively(expandedRoot, context);

        return new RecursiveDiscovery(paths, failedSubtrees, errors);
    }

    /// <summary>
    /// Get all sub-directory paths within <paramref name="rootPath"/> that contain .git folders we care about.
    /// </summary>
    public static void GetRepositoryPaths(string rootPath, ICollection<string> paths,
        ICollection<string> ignoredPaths, IUpdateHandler? updateHandler = null,
        bool? skipHiddenDirectories = null) =>
        GetRepositoryPaths(rootPath, paths, ignoredPaths, updateHandler, skipHiddenDirectories, includeTempDirectories: null);

    /// <summary>
    /// Get all sub-directory paths within <paramref name="rootPath"/> that contain .git folders we care about.
    /// </summary>
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

        var configuration = new GitWizardConfiguration();
        if (skipHiddenDirectories.HasValue)
            configuration.SkipHiddenDirectories = skipHiddenDirectories.Value;
        if (includeTempDirectories.HasValue)
            configuration.IncludeTempDirectories = includeTempDirectories.Value;
        foreach (var ignored in ignoredPaths)
            configuration.IgnoredPaths.Add(ignored);

        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, true, []);
        var result = DiscoverRecursively(request, expanded, updateHandler, CancellationToken.None);
        foreach (var candidatePath in result.Paths)
            paths.Add(candidatePath);

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
        ICollection<string> FailedSubtrees,
        IDictionary<string, string> Errors,
        ICollection<string> IgnoredPaths,
        bool IncludeGitFiles,
        IUpdateHandler? UpdateHandler,
        bool? SkipHiddenDirectories,
        bool ExcludeTempDirectories,
        CancellationToken CancellationToken);

    static void FindGitRepositoriesRecursively(string rootPath, DiscoveryContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

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

            string[] directories;
            try
            {
                directories = DirectoryEnumeratorOverride is not null
                    ? DirectoryEnumeratorOverride(rootPath)
                    : Directory.GetDirectories(rootPath);
                if (directories.Length == 0)
                    return;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException)
            {
                lock (context.FailedSubtrees)
                {
                    var norm = NormalizePath(rootPath);
                    context.FailedSubtrees.Add(norm);
                    context.Errors[norm] = exception.Message;
                }
                return;
            }

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = context.CancellationToken
            };
            Parallel.ForEach(directories, parallelOptions, subDirectory =>
            {
                ProcessSubDirectory(subDirectory, rootPath, context);
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
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
            context.CancellationToken.ThrowIfCancellationRequested();
            var normalizedSubDirectory = NormalizePath(subDirectory);
            var split = subDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var lastDirectory = split.Last();

            if (string.Equals(lastDirectory, ".git", StringComparison.OrdinalIgnoreCase))
            {
                lock (context.Paths)
                {
                    context.Paths.Add(NormalizePath(rootPath));
                }
                return;
            }

            if (lastDirectory.StartsWith('.') && context.SkipHiddenDirectories != false)
                return;

            if (OperatingSystem.IsWindows())
            {
                var directoryInfo = new DirectoryInfo(subDirectory);
                if (directoryInfo.Attributes.HasFlag(FileAttributes.Hidden) && context.SkipHiddenDirectories != false)
                    return;

                if (directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
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
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException)
        {
            lock (context.FailedSubtrees)
            {
                var norm = NormalizePath(subDirectory);
                context.FailedSubtrees.Add(norm);
                context.Errors[norm] = exception.Message;
            }
            GitWizardLog.Log($"Skipping directory during scan: {exception.Message}", GitWizardLog.LogType.Verbose);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GitWizardLog.Log($"Skipping directory during scan: {exception.Message}", GitWizardLog.LogType.Verbose);
        }
    }

    internal static bool IsBareGitRepository(string path)
    {
        // Must exclude any candidate whose path is inside a .git directory
        var segments = WatchPath.Parse(path).Comparison.Split('/');
        if (segments.Any(s => string.Equals(s, ".git", StringComparison.OrdinalIgnoreCase)))
            return false;

        return File.Exists(Path.Combine(path, "HEAD")) &&
               Directory.Exists(Path.Combine(path, "objects")) &&
               Directory.Exists(Path.Combine(path, "refs"));
    }
}
