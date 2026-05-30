using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitWizard;

public class GitWizardConfiguration
{
    static GitWizardConfiguration? _globalConfiguration;
    static readonly object ConfigurationLock = new();

    // Cached options reused across serialization (CA1869: avoid allocating a new
    // JsonSerializerOptions per call).
    static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = true
    };

    public SortedSet<string> SearchPaths { get; set; } = new();
    public SortedSet<string> IgnoredPaths { get; set; } = new();

    /// <summary>
    /// Full path to the Fork executable. Null means use the default location.
    /// </summary>
    [JsonPropertyName("forkPath")]
    public string? ForkPath { get; set; }

    public static string GetGlobalConfigurationPath()
    {
        return Path.Combine(GitWizardApi.GetLocalFilesPath(), "config.json");
    }

    public static GitWizardConfiguration GetGlobalConfiguration()
    {
        lock (ConfigurationLock)
        {
            if (_globalConfiguration == null)
            {
                _globalConfiguration = GetConfigurationAtPath(GetGlobalConfigurationPath())
                                     ?? CreateDefaultConfiguration();
            }
        }

        return _globalConfiguration;
    }

    public static void SaveGlobalConfiguration(GitWizardConfiguration configuration)
    {
        configuration.Save(GetGlobalConfigurationPath());
    }

    public static async Task SaveGlobalConfigurationAsync(GitWizardConfiguration configuration)
    {
        await configuration.SaveAsync(GetGlobalConfigurationPath()).ConfigureAwait(false);
    }

    public static async Task<GitWizardConfiguration?> GetConfigurationAtPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var jsonText = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<GitWizardConfiguration>(jsonText);
        }
        catch
        {
            // ignored
        }

        return null;
    }

    public static GitWizardConfiguration? GetConfigurationAtPath(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var jsonText = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GitWizardConfiguration>(jsonText);
        }
        catch
        {
            // ignored
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
            configuration.IgnoredPaths.Add("%WINDIR%");
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
            File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Error: Failed to save configuration to path: {path}.");
        }
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        try
        {
            var jsonText = JsonSerializer.Serialize(this, SerializerOptions);
            await File.WriteAllTextAsync(path, jsonText, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Error: Failed to save configuration to path: {path}.");
        }
    }

    public static async Task<GitWizardConfiguration> GetGlobalConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (_globalConfiguration != null)
            return _globalConfiguration;

        lock (ConfigurationLock)
        {
            if (_globalConfiguration != null)
                return _globalConfiguration;
        }

        var config = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        _globalConfiguration = config ?? CreateDefaultConfiguration();
        return _globalConfiguration;
    }

    static async Task<GitWizardConfiguration?> LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        var path = GetGlobalConfigurationPath();
        return await GetConfigurationAtPathAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public void GetRepositoryPaths(ICollection<string> paths, IUpdateHandler? updateHandler = null)
    {
        Parallel.ForEach(SearchPaths, path =>
        {
            GitWizardApi.GetRepositoryPaths(path, paths, IgnoredPaths, updateHandler);
        });
    }
}
