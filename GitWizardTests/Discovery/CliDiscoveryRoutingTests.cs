using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests.Discovery;

[TestFixture]
[NonParallelizable]
public class CliDiscoveryRoutingTests
{
    static IEnumerable<string> ExpectedRoots(CliDiscoveryFixture fixture, string command)
    {
        var shared = GitWizardApi.GetPrCrewReposPath();
        return command is "tags" or "branches" && Directory.Exists(shared)
            ? fixture.Configuration.SearchPaths.Append(shared) : fixture.Configuration.SearchPaths;
    }

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
        fixture.Dependencies.CrawlGuard = root => throw new AssertionException($"Separate crawler invoked for {root}");
        await fixture.RunAsync();
        Assert.That(fixture.OpenedDriveSets, Is.Not.Empty);
        foreach (var drives in fixture.OpenedDriveSets)
        {
            Assert.That(drives, Is.EquivalentTo(ExpectedRoots(fixture, command)).IgnoreCase);
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
            Assert.That(drives, Is.EquivalentTo(ExpectedRoots(fixture, command)).IgnoreCase);
        }
    }

    [TestCase("report")]
    [TestCase("scan")]
    [TestCase("sweep")]
    [TestCase("tags")]
    [TestCase("branches")]
    [TestCase("watch")]
    public async Task NoMft_OpensUncachedDirectoryIndexes(string command)
    {
        await using var fixture = new CliDiscoveryFixture(command, noMft: true);
        fixture.Configuration.SearchPaths.Clear();
        var treePath = fixture.Dependencies.IndexFixture.TreePath;
        fixture.Configuration.SearchPaths.Add(treePath);

        await fixture.RunAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Dependencies.CrawledRoots, Is.Empty);
            Assert.That(fixture.OpenedDriveSets, Is.Not.Empty);
            Assert.That(fixture.Dependencies.OpenedCacheModes, Is.All.EqualTo(CacheMode.Disabled));
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
        fixture.Dependencies.CustomOpenIndexAsync = StatusSessions.Opener(indexFixture, ('C', SyntheticDriveBehavior.ProducerFails));

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
        fixture.Dependencies.CustomOpenIndexAsync = StatusSessions.Opener(indexFixture, ('C', SyntheticDriveBehavior.ProducerFails));

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
        fixture.Dependencies.CustomOpenIndexAsync = StatusSessions.Opener(indexFixture, ('C', SyntheticDriveBehavior.ProducerFails));

        var exitCode = await fixture.RunAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(fixture.Stderr.ToString(), Does.Contain("Drive C failure"));
        }
    }
}
