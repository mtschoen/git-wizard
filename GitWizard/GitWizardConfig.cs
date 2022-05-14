using System.Runtime.InteropServices;
using System.Text.Json;

namespace GitWizard
{
    public class GitWizardConfig
    {
        static GitWizardConfig? _globalConfig;

        public SortedSet<string> SearchPaths { get; set; } = new();
        public SortedSet<string> IgnoredPaths { get; set; } = new();

        public static string GetGlobalConfigPath()
        {
            return Path.Combine(GitWizardAPI.GetCachePath(), "config.json");
        }

        public static GitWizardConfig GetGlobalConfig()
        {
            if (_globalConfig == null)
            {
                var globalConfigPath = GetGlobalConfigPath();
                if (File.Exists(globalConfigPath))
                {
                    // TODO: Async config load (file read)
                    try
                    {
                        var jsonText = File.ReadAllText(globalConfigPath);
                        _globalConfig = JsonSerializer.Deserialize<GitWizardConfig>(jsonText);
                    }
                    catch { }
                }
            }

            return _globalConfig ??= CreateDefaultConfig();
        }

        static GitWizardConfig CreateDefaultConfig()
        {
            var config = new GitWizardConfig();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                config.SearchPaths.Add("%USERPROFILE%");
                config.IgnoredPaths.Add("%APPDATA%");
            }
            else
            {
                config.SearchPaths.Add("~");
            }

            return config;
        }

        public void Save(string path)
        {
            var cacheDirectory = GitWizardAPI.GetCachePath();
            if (!Directory.Exists(cacheDirectory))
                Directory.CreateDirectory(cacheDirectory);

            // TODO: Async config save
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
    }
}
