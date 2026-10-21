
namespace GitWizardTests.TestSupport;

internal sealed class LocalFilesSandbox : IDisposable
{
    readonly TempDirectory _directory = new("GitWizardHome-");
    readonly string? _previousHome = Environment.GetEnvironmentVariable("GITWIZARD_HOME");
    bool _disposed;
    public string Path => _directory.Path;

    public LocalFilesSandbox()
    {
        try
        {
            Environment.SetEnvironmentVariable("GITWIZARD_HOME", Path);
            TestUtilities.ResetStaticCaches();
        }
        catch
        {
            Environment.SetEnvironmentVariable("GITWIZARD_HOME", _previousHome ?? TestUtilities.DefaultHome);
            _directory.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            TestUtilities.ResetStaticCaches();
            TestUtilities.ClearLocalFilesRedirect(Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITWIZARD_HOME", _previousHome ?? TestUtilities.DefaultHome);
            TestUtilities.ResetStaticCaches();
            _directory.Dispose();
        }
    }
}
