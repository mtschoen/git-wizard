using System.Text.Json;
using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests;

[TestFixture]
public class ReportDiscoverySessionTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public async Task GenerateReportAsync_CachedRepositoryPaths_DoesNotRetainIndex()
    {
        await using var report = await GitWizardReport.GenerateReportAsync(
            new GitWizardConfiguration(), new List<string>(),
            options: new GitWizardReportOptions { RetainDiscoveryIndex = true });
        Assert.That(report.DiscoveryIndex, Is.Null);
    }

    [Test]
    public async Task GenerateReportAsync_NoMft_DoesNotRetainIndex()
    {
        await using var report = await GitWizardReport.GenerateReportAsync(
            new GitWizardConfiguration(),
            options: new GitWizardReportOptions { NoMft = true, RetainDiscoveryIndex = true });
        Assert.That(report.DiscoveryIndex, Is.Null);
    }

    [Test]
    public async Task DetachDiscoveryIndex_ClearsReportPropertiesAndTransfersOwnership()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        var report = new GitWizardReport(new GitWizardConfiguration());
        try
        {
            await report.SetDiscoveryIndexAsync(fixture.Owner);
            var detached = report.DetachDiscoveryIndex();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(detached, Is.SameAs(fixture.Owner));
                Assert.That(report.DiscoveryIndex, Is.Null);
            }
            Assert.That(report.DetachDiscoveryIndex(), Is.Null);
        }
        finally
        {
            await report.DisposeAsync();
        }
        Assert.That(fixture.Owner.IsDisposed, Is.False);
    }

    [Test]
    public async Task DisposeAsync_DisposesDiscoveryIndexSafely()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        await using var replacement = new IndexOwnerFixture();
        await replacement.StartAsync();
        var report = new GitWizardReport(new GitWizardConfiguration());
        await report.SetDiscoveryIndexAsync(fixture.Owner);
        await report.SetDiscoveryIndexAsync(replacement.Owner);
        Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
        await report.DisposeAsync();
        await report.DisposeAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(replacement.ConnectionDisposals, Is.EqualTo(1));
            Assert.That(report.DiscoveryIndex, Is.Null);
        }
    }

    [Test]
    public async Task JsonSerialization_IgnoresDiscoveryProperties()
    {
        await using var report = new GitWizardReport(new GitWizardConfiguration());
        report.DiscoveryInventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.CompleteEmpty,
            Route = DiscoveryRoute.Indexed,
            Paths = [],
            RequestedNtfsDrives = [],
            Observations = [],
            RootStatuses = [],
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };
        var json = JsonSerializer.Serialize(report);
        Assert.That(json, Does.Not.Contain("DiscoveryIndex"));
        Assert.That(json, Does.Not.Contain("DiscoveryInventory"));
    }

    [Test]
    public async Task PopulatedIndexDetachesWithoutDisposal()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        var report = new GitWizardReport(new GitWizardConfiguration());
        await report.SetDiscoveryIndexAsync(fixture.Owner);
        var owner = report.DetachDiscoveryIndex();
        await report.DisposeAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(owner!.IndexSet.Search(new SearchQuery(".git", NameMatchMode.Exact)).Single().Parent!.Value.Name, Is.EqualTo("repo"));
            Assert.That(fixture.ConnectionDisposals, Is.Zero);
        }
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
    }

    [Test]
    public async Task PopulatedIndexIsNotSerialized()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        await using var report = new GitWizardReport(new GitWizardConfiguration());
        await report.SetDiscoveryIndexAsync(fixture.Owner);
        Assert.That(report.DiscoveryIndex!.Search(new SearchQuery(".git", NameMatchMode.Exact)).Single().Parent!.Value.Name, Is.EqualTo("repo"));
        var json = JsonSerializer.Serialize(report);
        Assert.That(json, Does.Not.Contain("DiscoveryIndex"));
        Assert.That(json, Does.Not.Contain(".git"));
        Assert.That(json, Does.Not.Contain("ScanErrors"));
        Assert.That(json, Does.Not.Contain("Drives"));
    }
}
