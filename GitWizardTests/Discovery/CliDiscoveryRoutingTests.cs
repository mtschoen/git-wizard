using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Discovery;

[TestFixture]
[NonParallelizable]
public class CliDiscoveryRoutingTests
{
    [SetUp]
    public void SetUp()
    {
        Program.ResetWatchDependenciesToDefault();
        Program._isWindows = () => true;
        Program._isLinux = () => false;
        Program._isElevated = () => false;
        Program._startProcess = _ => throw new AssertionException("Discovery routing must not launch a process.");
    }

    [TearDown]
    public void TearDown() => Program.ResetWatchDependenciesToDefault();

    [TestCase("report")]
    [TestCase("scan")]
    [TestCase("sweep")]
    [TestCase("tags")]
    [TestCase("branches")]
    [TestCase("watch")]
    public async Task WindowsEntryPointDoesNotCrawlNtfs(string command)
    {
        await using var fixture = new CliDiscoveryFixture(command);
        fixture.Dependencies.CrawlGuard = root => Assert.That(root, Does.StartWith(@"Z:\shared"));
        await fixture.RunAsync();
        Assert.That(fixture.OpenedDriveSets, Is.Not.Empty);
        foreach (var drives in fixture.OpenedDriveSets)
            Assert.That(drives, Is.EquivalentTo(new[] { "C" }));
    }

    [TestCase("report")]
    [TestCase("scan")]
    [TestCase("sweep")]
    [TestCase("tags")]
    [TestCase("branches")]
    [TestCase("watch")]
    public async Task WindowsNtfsOnly_DoesNotInvokeCrawler(string command)
    {
        await using var fixture = new CliDiscoveryFixture(command);
        fixture.Configuration.SearchPaths.Clear();
        fixture.Configuration.SearchPaths.Add(fixture.Dependencies.IndexFixture.TreePath);
        fixture.Dependencies.CrawlGuard = root => throw new AssertionException($"Crawler invoked unexpectedly for {root}");

        await fixture.RunAsync();

        Assert.That(fixture.Dependencies.CrawledRoots, Is.Empty);
        Assert.That(fixture.OpenedDriveSets, Is.Not.Empty);
        foreach (var drives in fixture.OpenedDriveSets)
            Assert.That(drives, Is.EquivalentTo(new[] { "C" }));
    }

    [TestCase("report")]
    [TestCase("scan")]
    [TestCase("sweep")]
    [TestCase("tags")]
    [TestCase("branches")]
    [TestCase("watch")]
    public async Task NoMft_ExplicitCrawlingIsUsed(string command)
    {
        await using var fixture = new CliDiscoveryFixture(command, noMft: true);
        fixture.Configuration.SearchPaths.Clear();
        var treePath = fixture.Dependencies.IndexFixture.TreePath;
        fixture.Configuration.SearchPaths.Add(treePath);

        await fixture.RunAsync();

        Assert.That(fixture.Dependencies.CrawledRoots, Does.Contain(treePath).IgnoreCase);
        if (command != "watch")
        {
            Assert.That(fixture.OpenedDriveSets, Is.Empty, "MFT discovery must not be opened for ordinary commands under --no-mft");
        }
        else
        {
            Assert.That(fixture.OpenedDriveSets, Is.Not.Empty, "Watch still arms NTFS for journal watch");
        }
    }

    static DriveStatus CreateDriveStatus(char letter, DriveState state, string? failureMessage = null) =>
        SyntheticNotifications.CreateDriveStatus(letter, state, BlockSource.ProducedByScan, 10, false, DateTime.UtcNow) with { FailureKind = DriveFailureKind.None, WatchSupported = state == DriveState.Ready, MftProducerFailureMessage = failureMessage };

    [TestCase("report")]
    [TestCase("sweep")]
    [TestCase("tags")]
    [TestCase("branches")]
    public async Task IncompleteDiscovery_RefusesPublication_Exits3(string command)
    {
        await using var fixture = new CliDiscoveryFixture(command);
        fixture.Configuration.SearchPaths.Clear();
        fixture.Configuration.SearchPaths.Add(fixture.Dependencies.IndexFixture.TreePath);

        var indexFixture = fixture.Dependencies.IndexFixture;
        fixture.Dependencies.CustomOpenIndexAsync = async (drives, _, _, _) =>
        {
            var driveChars = drives.Select(d => d[0]).ToArray();
            var index = await indexFixture.OpenIndexAsync(driveChars).ConfigureAwait(false);
            var indexedDrives = drives.Select(d => new IndexedDrive(d[0], indexFixture.TreePath, 1)).ToList();
            var session = new MftIndexSession(index, new CountingOwner(), drives, indexedDrives, new Dictionary<string, string>());
            session.DriveStatusesOverride = () =>
            [
                new MftDriveStatus("C", CreateDriveStatus('C', DriveState.Failed, "Drive C failure"), null)
            ];
            return session;
        };

        var exitCode = await fixture.RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(3));
            Assert.That(fixture.Stderr.ToString(), Does.Contain("Drive C failure"));
        });
    }

    [Test]
    public async Task ScanOnly_IncompleteDiscovery_Exits1()
    {
        await using var fixture = new CliDiscoveryFixture("scan");
        fixture.Configuration.SearchPaths.Clear();
        fixture.Configuration.SearchPaths.Add(fixture.Dependencies.IndexFixture.TreePath);

        var indexFixture = fixture.Dependencies.IndexFixture;
        fixture.Dependencies.CustomOpenIndexAsync = async (drives, _, _, _) =>
        {
            var driveChars = drives.Select(d => d[0]).ToArray();
            var index = await indexFixture.OpenIndexAsync(driveChars).ConfigureAwait(false);
            var indexedDrives = drives.Select(d => new IndexedDrive(d[0], indexFixture.TreePath, 1)).ToList();
            var session = new MftIndexSession(index, new CountingOwner(), drives, indexedDrives, new Dictionary<string, string>());
            session.DriveStatusesOverride = () =>
            [
                new MftDriveStatus("C", CreateDriveStatus('C', DriveState.Failed, "Drive C scan failure"), null)
            ];
            return session;
        };

        var exitCode = await fixture.RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(fixture.Stderr.ToString(), Does.Contain("Drive C scan failure"));
        });
    }
}
