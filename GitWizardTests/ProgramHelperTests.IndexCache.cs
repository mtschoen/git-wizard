using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

public partial class ProgramEntryPointTests
{
    [Test]
    public void ClearIndexCacheParsesWithoutChangingExistingClearFlag()
    {
        var result = CliParser.Parse(["--clear-index-cache"]);
        Assert.That(result.HasError, Is.False);
        // Reflection lets this compile before the new property exists.
        Assert.That(result.GetType().GetProperty("ClearIndexCache")?.GetValue(result), Is.EqualTo(true));
        Assert.That(result.ClearCache, Is.False);
    }

    [Test, NonParallelizable]
    public async Task ClearIndexCacheReportsLiveOwnerThenSucceedsAfterRelease()
    {
        var home = TestUtilities.RedirectLocalFilesToTemp();
        try
        {
            using var files = new IndexCacheFixture(GitWizardApi.GetLocalFilesPath());
            var repositoryList = GitWizardApi.GetCachedRepositoryListPath();
            await File.WriteAllTextAsync(repositoryList, "repository sentinel");
            await using (var owner = await files.OpenOwnerAsync())
            {
                var result = await RunCliAsync("--clear-index-cache", "--silent", "--watch");
                Assert.That(result.ExitCode, Is.EqualTo(1));
                Assert.That(result.StandardOutput, Does.Contain("X: (00000001)"));
                Assert.That(result.StandardOutput, Does.Contain("InUse"));
                Assert.That(result.StandardOutput, Does.Contain("Y: (00000001)"));
                Assert.That(result.StandardOutput, Does.Contain("Deleted"));
                Assert.That(result.StandardOutput, Does.Contain("Cache inspection"));
                Assert.That(result.StandardOutput, Does.Not.Contain("Session Start Message"));
                Assert.That(result.StandardError, Does.Contain("incomplete"));
                Assert.That(File.Exists(files.OwnedPath), Is.True);
                Assert.That(File.Exists(files.FreePath), Is.False);
                Assert.That(File.Exists(files.OwnedPath + ".lock"), Is.True);
                Assert.That(File.Exists(files.FreePath + ".lock"), Is.True);
                Assert.That(owner.Root('X').IsDirectory, Is.True);
            }
            var retry = await RunCliAsync("--clear-index-cache");
            Assert.That(retry.ExitCode, Is.Zero);
            Assert.That(retry.StandardError, Is.Empty);
            Assert.That(retry.StandardOutput, Does.Contain("No canonical cached blocks remain"));
            Assert.That(await File.ReadAllTextAsync(repositoryList), Is.EqualTo("repository sentinel"));
            Assert.That(File.Exists(files.OwnedPath + ".lock"), Is.True);
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(home);
        }
    }
}
