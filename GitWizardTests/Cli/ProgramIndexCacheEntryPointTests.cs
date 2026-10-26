using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;
using static GitWizardTests.CliProcess;

namespace GitWizardTests;

// Entry-point tests are separated by command concern because the combined fixture exceeds 500 lines.
public class ProgramIndexCacheEntryPointTests
{
    [Test]
    public void ClearIndexCacheParsesWithoutChangingExistingClearFlag()
    {
        var result = CliParser.Parse(["--clear-index-cache"]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasParseError, Is.False);
            Assert.That(result.Command, Is.EqualTo(CommandKind.ClearIndexCache));
            Assert.That(result.ClearCache, Is.False);
        }
    }

    [Test, NonParallelizable]
    public async Task ClearIndexCacheReportsLiveOwnerThenSucceedsAfterRelease()
    {
        using var sandbox = new LocalFilesSandbox();
        using var files = new IndexCacheFixture(GitWizardApi.GetLocalFilesPath());
        var repositoryList = GitWizardApi.GetCachedRepositoryListPath();
        await File.WriteAllTextAsync(repositoryList, "repository sentinel");
        await using (var owner = await files.OpenOwnerAsync())
        {
            var result = await RunCliAsync("--clear-index-cache", "--silent", "--watch");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.ExitCode, Is.EqualTo(1));
                Assert.That(result.StandardOutput, Does.Contain("X: (00000001)"));
            }
            Assert.That(result.StandardOutput, Does.Contain("InUse"));
            Assert.That(result.StandardOutput, Does.Contain("Y: (00000001)"));
            Assert.That(result.StandardOutput, Does.Contain("Deleted"));
            Assert.That(result.StandardOutput, Does.Contain("Cache inspection"));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.StandardOutput, Does.Not.Contain("Session Start Message"));
                Assert.That(result.StandardError, Does.Contain("incomplete"));
                Assert.That(File.Exists(files.OwnedPath), Is.True);
                Assert.That(File.Exists(files.FreePath), Is.False);
                Assert.That(File.Exists(files.OwnedPath + ".lock"), Is.True);
                Assert.That(File.Exists(files.FreePath + ".lock"), Is.True);
                Assert.That(owner.Root('X').IsDirectory, Is.True);
            }
        }
        var retry = await RunCliAsync("--clear-index-cache");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(retry.ExitCode, Is.Zero);
            Assert.That(retry.StandardError, Is.Empty);
            Assert.That(retry.StandardOutput, Does.Contain("No canonical cached blocks remain"));
            Assert.That(await File.ReadAllTextAsync(repositoryList), Is.EqualTo("repository sentinel"));
            Assert.That(File.Exists(files.OwnedPath + ".lock"), Is.True);
        }
    }

    [Test]
    public void IndexCacheStatusParsesAsReadOnly()
    {
        var result = CliParser.Parse(["--index-cache-status"]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasParseError, Is.False);
            Assert.That(result.Command, Is.EqualTo(CommandKind.IndexCacheStatus));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Command, Is.Not.EqualTo(CommandKind.ClearIndexCache));
            Assert.That(result.ClearCache, Is.False);
            Assert.That(result.DeleteAllLocalFiles, Is.False);
        }
    }

    [TestCase("--clear-index-cache")]
    [TestCase("--clear-cache")]
    [TestCase("--delete-all-local-files")]
    public void IndexCacheStatusRejectsDestructiveCombinations(string flag) =>
        Assert.That(CliParser.Parse(["--index-cache-status", flag]).HasParseError, Is.True);

    [Test]
    public async Task IndexCacheStatusWriterReportsInspectionError()
    {
        using var sandbox = new LocalFilesSandbox();
        try
        {
            using var files = new PackedIndexFixture();
            await using var output = new StringWriter();
            await using var error = new StringWriter();
            var snapshot = new MftIndexCacheInspection(new FrozenCacheClock(), true,
                _ => throw new IOException("unreadable inventory")).Capture(files.DirectoryPath);
            Assert.That(await Program.WriteIndexCacheStatusAsync(snapshot, output, error), Is.EqualTo(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(output.ToString(), Does.Contain("Error:"));
                Assert.That(error.ToString(), Does.Contain("unreadable inventory"));
            }
        }
        finally
        {
            GitWizardLog.CloseCurrentLogFile();
        }
    }

    [Test, NonParallelizable]
    public async Task IndexCacheStatusExitsBeforeWatchAndDiscovery()
    {
        using var sandbox = new LocalFilesSandbox();
        try
        {
            var result = await RunCliAsync("--index-cache-status", "--watch", "--setup-defender", "--silent");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.ExitCode, Is.Zero);
                Assert.That(result.StandardError, Is.Empty);
                Assert.That(result.StandardOutput, Does.Not.Contain("Session Start Message"));
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.StandardOutput, Does.Contain(OperatingSystem.IsWindows()
                            ? "No cached blocks" : "not applicable"));
                Assert.That(Directory.Exists(MftIndexCachePolicy.GetCacheRoot(GitWizardApi.GetLocalFilesPath())), Is.False);
                Assert.That(File.Exists(GitWizardApi.GetCachedRepositoryListPath()), Is.False);
            }
        }
        finally
        {
            GitWizardLog.CloseCurrentLogFile();
        }
    }

    [Test, NonParallelizable, Platform("Win")]
    public async Task IndexCacheStatusDoesNotClaimTheParentProcessOwner()
    {
        using var sandbox = new LocalFilesSandbox();
        try
        {
            using var files = new IndexCacheFixture(GitWizardApi.GetLocalFilesPath());
            await using (var owner = await files.OpenOwnerAsync())
            {
                var held = await RunCliAsync("--index-cache-status");
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(held.ExitCode, Is.Zero);
                    Assert.That(held.StandardOutput, Does.Contain("holder identity unavailable"));
                }
                Assert.That(held.StandardOutput, Does.Contain("serial 00000001"));
                Assert.That(held.StandardOutput, Does.Contain("Invalid"));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(held.StandardOutput, Does.Not.Contain("this session owns the cache"));
                    Assert.That(owner.Root('X').IsDirectory, Is.True);
                }
            }
            var canonical = await File.ReadAllBytesAsync(files.OwnedPath);
            var invalid = await File.ReadAllBytesAsync(files.FreePath);
            var available = await RunCliAsync("--index-cache-status");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(available.ExitCode, Is.Zero);
                Assert.That(available.StandardOutput, Does.Contain("Drive X: serial 00000001; Available"));
                Assert.That(await File.ReadAllBytesAsync(files.OwnedPath), Is.EqualTo(canonical));
                Assert.That(await File.ReadAllBytesAsync(files.FreePath), Is.EqualTo(invalid));
            }
        }
        finally
        {
            GitWizardLog.CloseCurrentLogFile();
        }
    }
}
