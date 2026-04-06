using GitWizard;

namespace GitWizardTests;

public class GitWizardReportTests
{
    [Test]
    public void Report_HasSchemaVersion()
    {
        var report = new GitWizardReport();
        Assert.That(report.SchemaVersion, Is.EqualTo("1.0"));
    }

    [Test]
    public void Report_SchemaVersionSerializesToJson()
    {
        var report = new GitWizardReport();
        var json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.That(json, Does.Contain("\"SchemaVersion\":\"1.0\""));
    }

    [Test]
    public void RefreshConcurrencyTest()
    {
        GitWizardLog.SilentMode = true;
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var repositoryPaths = new SortedSet<string>();
        var report = new GitWizardReport(configuration);
        report.GetRepositoryPaths(repositoryPaths);
        Parallel.For(0, 10, _ => { report.Refresh(repositoryPaths); });
    }
}
