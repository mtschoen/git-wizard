using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

[TestFixture, NonParallelizable]
public class MainViewModelIndexCacheTests
{
    [Test]
    public async Task ClearDisplaysNewInventoryAndDoesNotDisposeLiveOwner()
    {
        var home = TestUtilities.RedirectLocalFilesToTemp();
        try
        {
            using var files = new IndexCacheFixture(GitWizardApi.GetLocalFilesPath());
            await using var owner = await files.OpenOwnerAsync();
            await using var viewModel = new MainViewModel(
                new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());
            await viewModel.InspectIndexCacheAsync();
            Assert.That(viewModel.IndexCacheText, Does.Contain("Invalid"));
            await viewModel.ClearIndexCacheAsync();
            Assert.That(viewModel.IndexCacheText, Does.Contain("InUse"));
            Assert.That(viewModel.IndexCacheText, Does.Contain("Deleted"));
            Assert.That(viewModel.IndexCacheText, Does.Not.Contain("Invalid"));
            Assert.That(viewModel.IndexCacheText, Does.Contain("incomplete"));
            Assert.That(viewModel.CanManageIndexCache, Is.True);
            Assert.That(File.Exists(files.OwnedPath), Is.True);
            Assert.That(File.Exists(files.FreePath), Is.False);
            Assert.That(File.Exists(files.FreePath + ".lock"), Is.True);
            Assert.That(owner.Root('X').IsDirectory, Is.True);
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(home);
        }
    }
}
