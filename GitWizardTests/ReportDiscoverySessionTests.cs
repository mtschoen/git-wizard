using System.Text.Json;
using GitWizard;

namespace GitWizardTests;

[TestFixture]
public class ReportDiscoverySessionTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public async Task GenerateReportAsync_CachedRepositoryPaths_DoesNotRetainSession()
    {
        var configuration = new GitWizardConfiguration();
        var repositoryPaths = new List<string>();

        var options = new GitWizardReportOptions
        {
            RetainDiscoverySession = true
        };

        await using var report = await GitWizardReport.GenerateReportAsync(
            configuration, repositoryPaths, options: options);

        Assert.That(report.DiscoverySession, Is.Null);
        Assert.That(report.DiscoveryScanRecords, Is.Null);
        Assert.That(report.DiscoveryDrives, Is.Null);
    }

    [Test]
    public async Task GenerateReportAsync_NoMft_DoesNotRetainSession()
    {
        var configuration = new GitWizardConfiguration();
        var options = new GitWizardReportOptions
        {
            NoMft = true,
            RetainDiscoverySession = true
        };

        await using var report = await GitWizardReport.GenerateReportAsync(
            configuration, options: options);

        Assert.That(report.DiscoverySession, Is.Null);
        Assert.That(report.DiscoveryScanRecords, Is.Null);
        Assert.That(report.DiscoveryDrives, Is.Null);
    }

    [Test]
    public async Task DetachDiscoverySession_ClearsReportPropertiesAndTransfersOwnership()
    {
        var configuration = new GitWizardConfiguration();
        await using var report = new GitWizardReport(configuration);

        var (session, records, drives) = report.DetachDiscoverySession();
        Assert.That(session, Is.Null);
        Assert.That(records, Is.Null);
        Assert.That(drives, Is.Null);
        Assert.That(report.DiscoverySession, Is.Null);
        Assert.That(report.DiscoveryScanRecords, Is.Null);
        Assert.That(report.DiscoveryDrives, Is.Null);
    }

    [Test]
    public async Task DisposeAsync_DisposesDiscoverySessionSafely()
    {
        var configuration = new GitWizardConfiguration();
        var report = new GitWizardReport(configuration);

        await report.DisposeAsync();

        Assert.That(report.DiscoverySession, Is.Null);
        Assert.That(report.DiscoveryScanRecords, Is.Null);
        Assert.That(report.DiscoveryDrives, Is.Null);
    }

    [Test]
    public void JsonSerialization_IgnoresDiscoveryProperties()
    {
        var configuration = new GitWizardConfiguration();
        var report = new GitWizardReport(configuration);

        var json = JsonSerializer.Serialize(report);

        Assert.That(json, Does.Not.Contain("DiscoverySession"));
        Assert.That(json, Does.Not.Contain("DiscoveryScanRecords"));
        Assert.That(json, Does.Not.Contain("DiscoveryDrives"));
    }
}
