using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

[TestFixture, NonParallelizable]
public class MainViewModelIndexCacheTests
{
    [Test]
    public async Task SidebarInspectRendersTheStatusWindowText()
    {
        using var sandbox = new LocalFilesSandbox();
        using var files = new IndexCacheFixture(GitWizardApi.GetLocalFilesPath());
        await using var viewModel = TestViewModels.Create();
        await viewModel.IndexCache.RefreshAsync();
        var snapshot = viewModel.IndexCache.Snapshot!;
        Assert.That(viewModel.IndexCache.Text, Is.EqualTo(MftIndexCacheStatusFormatter.Format(snapshot)));
        Assert.That(viewModel.IndexCache.Text, Does.Contain(OperatingSystem.IsWindows()
            ? "Cache directory: " + files.Cache
            : "MFT index cache is not applicable on this platform (Windows only)."));
    }

    [Test]
    public async Task ClearDisplaysNewInventoryAndDoesNotDisposeLiveOwner()
    {
        using var sandbox = new LocalFilesSandbox();
        using var files = new IndexCacheFixture(GitWizardApi.GetLocalFilesPath());
        await using var owner = await files.OpenOwnerAsync();
        await using var viewModel = TestViewModels.Create();
        await viewModel.IndexCache.RefreshAsync();
        Assert.That(viewModel.IndexCache.Text, Does.Contain(OperatingSystem.IsWindows()
            ? "Invalid"
            : "MFT index cache is not applicable on this platform (Windows only)."));
        await viewModel.IndexCache.ClearAsync();
        Assert.That(viewModel.IndexCache.Text, Does.Contain("InUse"));
        Assert.That(viewModel.IndexCache.Text, Does.Contain("Deleted"));
        Assert.That(viewModel.IndexCache.Text, Does.Not.Contain("Invalid"));
        Assert.That(viewModel.IndexCache.Text, Does.Contain("incomplete"));
        Assert.That(viewModel.IndexCache.IsIdle, Is.True);
        Assert.That(File.Exists(files.OwnedPath), Is.True);
        Assert.That(File.Exists(files.FreePath), Is.False);
        Assert.That(File.Exists(files.FreePath + ".lock"), Is.True);
        Assert.That(owner.Root('X').IsDirectory, Is.True);
    }
}
