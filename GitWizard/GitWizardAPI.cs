namespace GitWizard
{
    public static class GitWizardAPI
    {
        /// <summary>
        /// Get all subdirectory paths within <paramref name="rootPath"/> that contain .git folders we care about
        /// </summary>
        /// <param name="rootPath">The path to search</param>
        /// <param name="paths">List of strings to store the results</param>
        public static async Task GetRepositoryPaths(string rootPath, List<string> paths, IUpdateProgressString? updater = null)
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

            await FindGitRepositoriesRecursively(rootPath, paths, updater);
            updater?.UpdateProgress($"Found {paths.Count} repositories");
        }

        static async Task FindGitRepositoriesRecursively(string rootPath, List<string> paths, IUpdateProgressString? updater = null)
        {
            try
            {
                updater?.UpdateProgress(rootPath);
                foreach (var subDirectory in Directory.GetDirectories(rootPath))
                {
                    if (subDirectory.EndsWith(".git"))
                    {
                        lock (paths)
                        {
                            paths.Add(subDirectory);
                        }

                        continue;
                    }

                    await Task.Run(() => FindGitRepositoriesRecursively(subDirectory, paths, updater));
                }
            }
            catch (Exception e)
            {
                updater?.UpdateProgress($"Exception reading {rootPath}: {e.Message}");
                // Ignore exceptions like access failure
            }
        }
    }
}