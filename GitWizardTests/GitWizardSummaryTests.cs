using GitWizard;

namespace GitWizardTests;

public class GitWizardSummaryTests
{
    [Test]
    public void FromReport_CountsDirtyRepos()
    {
        GitWizardLog.SilentMode = true;
        var report = new GitWizardReport();
        var clean = new GitWizardRepository("c:\\clean");
        clean.Refresh();
        report.Repositories["c:\\clean"] = clean;

        var summary = GitWizardSummary.FromReport(report);

        Assert.That(summary.TotalRepositories, Is.EqualTo(1));
        Assert.That(summary.SchemaVersion, Is.EqualTo("1.1"));
    }

    [Test]
    public void NeedingAttention_IncludesDirtyAndUnpushed()
    {
        GitWizardLog.SilentMode = true;
        var report = new GitWizardReport();

        var repoPath = FindRepoRoot();
        var repository = new GitWizardRepository(repoPath);
        repository.Refresh();
        report.Repositories[repoPath] = repository;

        var summary = GitWizardSummary.FromReport(report);

        Assert.That(summary.TotalRepositories, Is.EqualTo(1));
        Assert.That(summary.NeedingAttention, Is.Not.Null);
    }

    static string FindRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory, ".git")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find git repo root from working directory");
    }
}
