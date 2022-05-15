using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitWizard;

public class GitWizardConfiguration
{
    static GitWizardConfiguration? _globalConfiguration;

    public SortedSet<string> SearchPaths { get; set; } = new();
    public SortedSet<string> IgnoredPaths { get; set; } = new();

    public static string GetGlobalConfigPath()
    {
        return Path.Combine(GitWizardApi.GetCachePath(), "config.json");
    }

    public static GitWizardConfiguration GetGlobalConfiguration()
    {
        _globalConfiguration ??= GetConfigurationAtPath(GetGlobalConfigPath());
        return _globalConfiguration ??= CreateDefaultConfig();
    }

    public static GitWizardConfiguration? GetConfigurationAtPath(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            // TODO: Async file read
            var jsonText = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GitWizardConfiguration>(jsonText);
        }
        catch
        {
            // ignored
            // TODO: Error feedback
        }

        return null;
    }


    static GitWizardConfiguration CreateDefaultConfig()
    {
        var configuration = new GitWizardConfiguration();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            configuration.SearchPaths.Add("%USERPROFILE%");
            configuration.IgnoredPaths.Add("%APPDATA%");
        }
        else
        {
            configuration.SearchPaths.Add("~");
        }

        return configuration;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        try
        {
            // TODO: Async config save
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
            }));
        }
        catch (Exception e)
        {
            if (!GitWizardApi.SilentMode)
            {
                Console.WriteLine($"Error: Failed to save configuration to path: {path}. Exception details to follow.");
                Console.WriteLine(e.Message);
                Console.WriteLine(e.StackTrace);
            }
        }
    }

    public async Task GetRepositoryPaths(ICollection<string> paths, Action<string>? onUpdate = null)
    {
        foreach (var path in SearchPaths)
        {
            await GitWizardApi.GetRepositoryPaths(path, paths, IgnoredPaths, onUpdate);
        }
    }
}
