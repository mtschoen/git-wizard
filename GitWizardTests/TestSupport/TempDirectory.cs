namespace GitWizardTests.TestSupport;

sealed class TempDirectory : IDisposable
{
    public static string SystemTempPath => System.IO.Path.GetTempPath();

    public string Path { get; }

    public TempDirectory(string prefix = "gw-test-", bool create = true)
    {
        Path = System.IO.Path.Combine(SystemTempPath, prefix + Guid.NewGuid().ToString("N"));
        if (create)
        {
            Directory.CreateDirectory(Path);
        }
    }

    public static void DeleteTree(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var normalizedTarget = System.IO.Path.TrimEndingDirectorySeparator(directory);
        var normalizedTemp = System.IO.Path.TrimEndingDirectorySeparator(SystemTempPath);
        if (string.Equals(normalizedTarget, normalizedTemp, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
        Directory.Delete(directory, recursive: true);
    }

    public void Dispose()
    {
        try
        {
            DeleteTree(Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve best-effort cleanup when an OS handle outlives a scratch fixture.
        }
    }
}
