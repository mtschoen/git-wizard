using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

public class RepositoryIndexRefreshReportTests
{
    [TestCase(RepositoryIndexRefreshReason.DeadIndexLock)]
    [TestCase(RepositoryIndexRefreshReason.StaleIndex)]
    public void Report_PreservesWarningAndPrintsAttention(RepositoryIndexRefreshReason reason)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "report.json");
        var report = new GitWizardReport();
        report.Repositories["repository"] = new GitWizardRepository("repository")
        {
            IndexRefreshReason = reason,
            StatusTimeSeconds = 2.5
        };

        report.Save(path);
        var restored = System.Text.Json.JsonSerializer.Deserialize<GitWizardReport>(File.ReadAllText(path))!;
        using var output = new StringWriter();
        ReportTextFormatter.Write(output, restored, false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.Repositories["repository"].IndexRefreshReason, Is.EqualTo(reason));
            Assert.That(restored.Repositories["repository"].StatusTimeSeconds, Is.EqualTo(2.5));
            Assert.That(output.ToString(), Does.Contain($"repository: index-needs-refresh ({reason})"));
        }
    }
}
