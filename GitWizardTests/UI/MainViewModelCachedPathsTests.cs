using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Refresh tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RefreshCoordinator.cs (UpdateCachedPathsAfterScanAsync).
public class MainViewModelCachedPathsTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;


    [Test]
    public async Task UpdateCachedPathsAfterScan_RemovesNonRepositoryPaths()
    {
        using var sandbox = new LocalFilesSandbox();
        var validPath = "/valid/repo";
        var stalePath = "/stale/not-a-repo";

        // Write a repositories.txt with both valid and stale paths.
        await GitWizardApi.SaveCachedRepositoryPathsAsync([validPath, stalePath]);

        // Call UpdateCachedPathsAfterScan with the stale path in NonRepositoryPaths.
        await RefreshCoordinator.UpdateCachedPathsAfterScanAsync(
            deletedPaths: [],
            renamedOldPaths: [],
            nonRepositoryPaths: [stalePath]);

        // The stale path should be removed from the cache file.
        var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync();
        Assert.That(cachedPaths, Is.Not.Null);
        Assert.That(cachedPaths, Does.Not.Contain(stalePath));
        Assert.That(cachedPaths, Does.Contain(validPath));

    }

    [Test]
    public async Task UpdateCachedPathsAfterScan_RemovesDeletedPaths()
    {
        using var sandbox = new LocalFilesSandbox();
        var validPath = "/valid/repo";
        var deletedPath = "/deleted/repo";

        await GitWizardApi.SaveCachedRepositoryPathsAsync([validPath, deletedPath]);

        await RefreshCoordinator.UpdateCachedPathsAfterScanAsync(
            deletedPaths: [deletedPath],
            renamedOldPaths: [],
            nonRepositoryPaths: []);

        var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync();
        Assert.That(cachedPaths, Is.Not.Null);
        Assert.That(cachedPaths, Does.Not.Contain(deletedPath));
        Assert.That(cachedPaths, Does.Contain(validPath));

    }

    [Test]
    public async Task UpdateCachedPathsAfterScan_RemovesAllThreeTypes()
    {
        using var sandbox = new LocalFilesSandbox();
        var healthyPath = "/healthy/repo";
        var deletedPath = "/deleted/repo";
        var stalePath = "/stale/not-a-repo";
        var renamedPath = "/old-name/repo";

        await GitWizardApi.SaveCachedRepositoryPathsAsync([healthyPath, deletedPath, stalePath, renamedPath]);

        await RefreshCoordinator.UpdateCachedPathsAfterScanAsync(
            deletedPaths: [deletedPath],
            renamedOldPaths: [renamedPath],
            nonRepositoryPaths: [stalePath]);

        var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync();
        Assert.That(cachedPaths, Is.Not.Null);
        Assert.That(cachedPaths!.Length, Is.EqualTo(1));
        Assert.That(cachedPaths[0], Is.EqualTo(healthyPath));

    }

    [Test]
    public async Task UpdateCachedPathsAfterScan_NoPathsToRemove_NoChange()
    {
        using var sandbox = new LocalFilesSandbox();
        var path = "/valid/repo";
        await GitWizardApi.SaveCachedRepositoryPathsAsync([path]);

        await RefreshCoordinator.UpdateCachedPathsAfterScanAsync([], [], []);

        var cachedPaths = await GitWizardApi.GetCachedRepositoryPathsAsync();
        Assert.That(cachedPaths, Is.Not.Null);
        Assert.That(cachedPaths!.Length, Is.EqualTo(1));
        Assert.That(cachedPaths[0], Is.EqualTo(path));

    }

    [Test]
    public async Task UpdateCachedPathsAfterScan_NoCachedFile_IsNoOp()
    {
        using var sandbox = new LocalFilesSandbox();
        // No repositories.txt exists; the method should not throw.
        await RefreshCoordinator.UpdateCachedPathsAfterScanAsync(
            deletedPaths: ["/some/deleted"],
            renamedOldPaths: [],
            nonRepositoryPaths: []);
        // If we got here without an exception, the test passes.

    }
}
