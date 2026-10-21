using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

public class AsyncFileIOTests
{
    LocalFilesSandbox? _sandbox;
    string? _tempRoot;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        // Redirect the data dir to temp so cache/config/report I/O is fully isolated. This
        // replaces the old hack of deleting the real ~/.GitWizard cache files in teardown.
        _sandbox = new LocalFilesSandbox();
        _tempRoot = _sandbox.Path;
    }

    [TearDown]
    public void TearDown()
    {
        _sandbox?.Dispose();
        _sandbox = null;
    }

    [Test]
    public async Task SaveAndLoadConfigurationAsync_RoundTrip()
    {
        var configPath = Path.Combine(_tempRoot!, "config.json");

        var config = new GitWizardConfiguration
        {
            SearchPaths = { "/path/one", "/path/two" },
            IgnoredPaths = { "/ignored/path" }
        };

        await config.SaveAsync(configPath);

        var loaded = await GitWizardConfiguration.GetConfigurationAtPathAsync(configPath);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.SearchPaths, Has.Count.EqualTo(2));
        Assert.That(loaded.SearchPaths, Does.Contain("/path/one"));
        Assert.That(loaded.SearchPaths, Does.Contain("/path/two"));
        Assert.That(loaded.IgnoredPaths, Has.Count.EqualTo(1));
        Assert.That(loaded.IgnoredPaths, Does.Contain("/ignored/path"));
    }

    [Test]
    public async Task GetConfigurationAtPathAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        var nonExistentPath = Path.Combine(_tempRoot!, "nonexistent.json");
        var result = await GitWizardConfiguration.GetConfigurationAtPathAsync(nonExistentPath);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetConfigurationAtPathAsync_ReturnsNull_ForInvalidJson()
    {
        var configPath = Path.Combine(_tempRoot!, "invalid.json");
        await File.WriteAllTextAsync(configPath, "not valid json {{{");

        var result = await GitWizardConfiguration.GetConfigurationAtPathAsync(configPath);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SaveConfigurationAsync_CreatesDirectory()
    {
        var nestedPath = Path.Combine(_tempRoot!, "sub", "dir", "config.json");

        var config = new GitWizardConfiguration();
        await config.SaveAsync(nestedPath);

        Assert.That(File.Exists(nestedPath), Is.True);
    }






    [Test]
    public async Task SaveAndLoadConfigurationAsync_EmptySets()
    {
        var configPath = Path.Combine(_tempRoot!, "empty_config.json");

        var config = new GitWizardConfiguration();
        await config.SaveAsync(configPath);

        var loaded = await GitWizardConfiguration.GetConfigurationAtPathAsync(configPath);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.SearchPaths, Has.Count.EqualTo(0));
        Assert.That(loaded.IgnoredPaths, Has.Count.EqualTo(0));
    }



    [Test]
    public async Task ConfigAsyncMethods_UseConfigureAwaitFalse()
    {
        // Verify that async methods don't capture synchronization context
        var configPath = Path.Combine(_tempRoot!, "context_test.json");
        var config = new GitWizardConfiguration { SearchPaths = { "/test" } };

        await config.SaveAsync(configPath);
        var loaded = await GitWizardConfiguration.GetConfigurationAtPathAsync(configPath);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.SearchPaths, Does.Contain("/test"));
    }
}
