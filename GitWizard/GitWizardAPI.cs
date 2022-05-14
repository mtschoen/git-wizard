using System.Runtime.InteropServices;

namespace GitWizard
{
    public static class GitWizardAPI
    {
        const string GitWizardFolder = ".GitWizard";
        public static string GetCachePath()
        {
            var homeFolder = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Environment.ExpandEnvironmentVariables("%USERPROFILE%")
                : "~";

            return Path.Combine(homeFolder, GitWizardFolder);
        }

        /// <summary>
        /// Get all sub-directory paths within <paramref name="rootPath"/> that contain .git folders we care about
        /// </summary>
        /// <param name="rootPath">The path to search</param>
        /// <param name="paths">List of strings to store the results</param>
        public static async Task GetRepositoryPaths(string rootPath, ICollection<string> paths, ICollection<string> ignoredPaths, Action<string>? onUpdate = null)
        {
            // If the string starts with a % we can try treating it as an environment variable
            if (rootPath.StartsWith("%"))
            {
                var path = Environment.ExpandEnvironmentVariables(rootPath);
                if (path != null)
                    rootPath = path;
            }

            if (!Directory.Exists(rootPath))
            {
                Console.WriteLine($"Could not get repository paths at {rootPath} because it is not a directory");
                return;
            }

            await FindGitRepositoriesRecursively(rootPath, paths, ignoredPaths,onUpdate);
            onUpdate?.Invoke($"Found {paths.Count} repositories");
        }

        static async Task FindGitRepositoriesRecursively(string rootPath, ICollection<string> paths, ICollection<string> ignoredPaths, Action<string>? onUpdate = null)
        {
            try
            {
                onUpdate?.Invoke(rootPath);
                foreach (var subDirectory in Directory.GetDirectories(rootPath))
                {
                    // TODO: Add config setting for ignoring hidden directories
                    var split = subDirectory.Split('\\');
                    if (split.Last().StartsWith("."))
                        continue;

                    var directoryInfo = new DirectoryInfo(subDirectory);
                    if (directoryInfo.Attributes.HasFlag(FileAttributes.Hidden))
                        continue;

                    if (ignoredPaths.Contains(subDirectory))
                        continue;

                    if (subDirectory.EndsWith(".git"))
                    {
                        lock (paths)
                        {
                            paths.Add(subDirectory);
                        }

                        continue;
                    }

                    await Task.Run(() => FindGitRepositoriesRecursively(subDirectory, paths, ignoredPaths, onUpdate));
                }
            }
            catch (Exception e)
            {
                onUpdate?.Invoke($"Exception reading {rootPath}: {e.Message}");
                // Ignore exceptions like access failure
            }
        }
    }
}