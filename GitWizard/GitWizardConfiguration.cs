using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitWizard;

public class GitWizardConfiguration
{
    static GitWizardConfiguration? _globalConfiguration;

    public SortedSet<string> SearchPaths { get; set; } = new();
    public SortedSet<string> IgnoredPaths { get; set; } = new();

    public static string GetGlobalConfigurationPath()
    {
        return Path.Combine(GitWizardApi.GetLocalFilesPath(), "config.json");
    }

    public static GitWizardConfiguration GetGlobalConfiguration()
    {
        _globalConfiguration ??= GetConfigurationAtPath(GetGlobalConfigurationPath());
        return _globalConfiguration ??= CreateDefaultConfiguration();
    }

    public static void SaveGlobalConfiguration(GitWizardConfiguration configuration)
    {
        configuration.Save(GetGlobalConfigurationPath());
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


    public static GitWizardConfiguration CreateDefaultConfiguration()
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
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
                WriteIndented = true
            }));
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Error: Failed to save configuration to path: {path}.");
        }
    }

    public void GetRepositoryPaths(ICollection<string> paths, IUpdateHandler? updateHandler = null)
    {
        Parallel.ForEach(SearchPaths, path =>
        {
            GitWizardApi.GetRepositoryPaths(path, paths, IgnoredPaths, updateHandler);
        });
    }
}
