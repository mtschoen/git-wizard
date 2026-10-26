using GitWizard.Watch;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Discovery;

[TestFixture]
[NonParallelizable]
public class CliDiscoveryRoutingTests
{
    [SetUp]
    public void SetUp()
    {
        VolumeChangeSourceFactory._isElevatedOverride = () => true;
    }

    [TearDown]
    public void TearDown() => VolumeChangeSourceFactory.ResetToDefaults();

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
        {
            Assert.That(drives, Is.EquivalentTo(["C"]));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Dependencies.CrawledRoots, Is.Empty);
            Assert.That(fixture.OpenedDriveSets, Is.Not.Empty);
        }
        foreach (var drives in fixture.OpenedDriveSets)
        {
            Assert.That(drives, Is.EquivalentTo(["C"]));
        }
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

    [TestCase("sweep")]
    [TestCase("tags")]
    [TestCase("branches")]
    public async Task IncompleteDiscovery_RefusesPublication_Exits3(string command)
    {
        await using var fixture = new CliDiscoveryFixture(command);
        fixture.Configuration.SearchPaths.Clear();
        fixture.Configuration.SearchPaths.Add(fixture.Dependencies.IndexFixture.TreePath);

        var indexFixture = fixture.Dependencies.IndexFixture;
        fixture.Dependencies.CustomOpenIndexAsync = (drives, _, _, _) => StatusSessions.OpenAsync(
            indexFixture, [.. drives.Select(drive => (drive[0], SyntheticDriveBehavior.ProducerFails))]);

        var exitCode = await fixture.RunAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(3));
            Assert.That(fixture.Stderr.ToString(), Does.Contain("Drive C failure"));
        }
    }

    [Test]
    public async Task IncompleteDiscovery_Report_KeepsDiagnosticsOffStderr_Exits3()
    {
        await using var fixture = new CliDiscoveryFixture("report");
        fixture.Configuration.SearchPaths.Clear();
        fixture.Configuration.SearchPaths.Add(fixture.Dependencies.IndexFixture.TreePath);

        var indexFixture = fixture.Dependencies.IndexFixture;
        fixture.Dependencies.CustomOpenIndexAsync = (drives, _, _, _) => StatusSessions.OpenAsync(
            indexFixture, [.. drives.Select(drive => (drive[0], SyntheticDriveBehavior.ProducerFails))]);

        var exitCode = await fixture.RunAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(3));
            Assert.That(fixture.Stdout.ToString(), Does.Contain("Drive C failure"));
            Assert.That(fixture.Stderr.ToString(), Does.Not.Contain("Drive C failure"));
        }
    }

    [Test]
    public async Task ScanOnly_IncompleteDiscovery_Exits1()
    {
        await using var fixture = new CliDiscoveryFixture("scan");
        fixture.Configuration.SearchPaths.Clear();
        fixture.Configuration.SearchPaths.Add(fixture.Dependencies.IndexFixture.TreePath);

        var indexFixture = fixture.Dependencies.IndexFixture;
        fixture.Dependencies.CustomOpenIndexAsync = (drives, _, _, _) => StatusSessions.OpenAsync(
            indexFixture, [.. drives.Select(drive => (drive[0], SyntheticDriveBehavior.ProducerFails))]);

        var exitCode = await fixture.RunAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(fixture.Stderr.ToString(), Does.Contain("Drive C failure"));
        }
    }
}
