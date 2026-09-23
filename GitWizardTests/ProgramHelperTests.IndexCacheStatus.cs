using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

public partial class ProgramEntryPointTests
{
    [Test]
    public void IndexCacheStatusParsesAsReadOnly()
    {
        var result = CliParser.Parse(["--index-cache-status"]);
        Assert.That(result.HasError, Is.False);
        Assert.That(result.IndexCacheStatus, Is.True);
        Assert.That(result.ClearIndexCache, Is.False);
        Assert.That(result.ClearCache, Is.False);
        Assert.That(result.DeleteAllLocalFiles, Is.False);
    }

    [TestCase("--clear-index-cache")]
    [TestCase("--clear-cache")]
    [TestCase("--delete-all-local-files")]
    public void IndexCacheStatusRejectsDestructiveCombinations(string flag) =>
        Assert.That(CliParser.Parse(["--index-cache-status", flag]).HasError, Is.True);

    [Test]
    public async Task IndexCacheStatusWriterReportsInspectionError()
    {
        var home = TestUtilities.RedirectLocalFilesToTemp();
        try
        {
            using var files = new PackedIndexFixture();
            using var output = new StringWriter();
            using var error = new StringWriter();
            var snapshot = new MftIndexCacheInspection(new FrozenCacheClock(), true,
                _ => throw new IOException("unreadable inventory")).Capture(files.DirectoryPath);
            Assert.That(await Program.WriteIndexCacheStatusAsync(snapshot, output, error), Is.EqualTo(1));
            Assert.That(output.ToString(), Does.Contain("Error:"));
            Assert.That(error.ToString(), Does.Contain("unreadable inventory"));
        }
        finally
        {
            GitWizardLog.CloseCurrentLogFile();
            TestUtilities.ClearLocalFilesRedirect(home);
        }
    }

    [Test, NonParallelizable]
    public async Task IndexCacheStatusExitsBeforeWatchAndDiscovery()
    {
        var home = TestUtilities.RedirectLocalFilesToTemp();
        try
        {
            var result = await RunCliAsync("--index-cache-status", "--watch", "--setup-defender", "--silent");
            Assert.That(result.ExitCode, Is.Zero);
            Assert.That(result.StandardError, Is.Empty);
            Assert.That(result.StandardOutput, Does.Not.Contain("Session Start Message"));
            Assert.That(result.StandardOutput, Does.Contain(OperatingSystem.IsWindows()
                ? "No cached blocks" : "not applicable"));
            Assert.That(Directory.Exists(MftIndexCachePolicy.GetCacheRoot(GitWizardApi.GetLocalFilesPath())), Is.False);
            Assert.That(File.Exists(GitWizardApi.GetCachedRepositoryListPath()), Is.False);
        }
        finally
        {
            GitWizardLog.CloseCurrentLogFile();
            TestUtilities.ClearLocalFilesRedirect(home);
        }
    }

    [Test, NonParallelizable, Platform("Win")]
    public async Task IndexCacheStatusDoesNotClaimTheParentProcessOwner()
    {
        var home = TestUtilities.RedirectLocalFilesToTemp();
        try
        {
            using var files = new IndexCacheFixture(GitWizardApi.GetLocalFilesPath());
            await using (var owner = await files.OpenOwnerAsync())
            {
                var held = await RunCliAsync("--index-cache-status");
                Assert.That(held.ExitCode, Is.Zero);
                Assert.That(held.StandardOutput, Does.Contain("holder identity unavailable"));
                Assert.That(held.StandardOutput, Does.Contain("serial 00000001"));
                Assert.That(held.StandardOutput, Does.Contain("Invalid"));
                Assert.That(held.StandardOutput, Does.Not.Contain("this session owns the cache"));
                Assert.That(owner.Root('X').IsDirectory, Is.True);
            }
            var canonical = await File.ReadAllBytesAsync(files.OwnedPath);
            var invalid = await File.ReadAllBytesAsync(files.FreePath);
            var available = await RunCliAsync("--index-cache-status");
            Assert.That(available.ExitCode, Is.Zero);
            Assert.That(available.StandardOutput, Does.Contain("Drive X: serial 00000001; Available"));
            Assert.That(await File.ReadAllBytesAsync(files.OwnedPath), Is.EqualTo(canonical));
            Assert.That(await File.ReadAllBytesAsync(files.FreePath), Is.EqualTo(invalid));
        }
        finally
        {
            GitWizardLog.CloseCurrentLogFile();
            TestUtilities.ClearLocalFilesRedirect(home);
        }
    }
}
