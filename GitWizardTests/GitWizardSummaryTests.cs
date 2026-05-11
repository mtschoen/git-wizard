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
    public void FromReport_CountsDownstreamBranches()
    {
        GitWizardLog.SilentMode = true;
        var report = new GitWizardReport();

        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var libgit = new LibGit2Sharp.Repository(fixture.Path);
        libgit.Branches.Add("feature/x", libgit.Head.Tip);

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        report.Repositories[fixture.Path] = repository;

        var summary = GitWizardSummary.FromReport(report);

        Assert.That(summary.TotalRepositories, Is.EqualTo(1));
        Assert.That(summary.SchemaVersion, Is.EqualTo("1.1"));
        Assert.That(summary.DownstreamBranches, Is.EqualTo(1));
        Assert.That(summary.NeedingAttention, Has.Count.EqualTo(1));
        Assert.That(summary.NeedingAttention[0].Reasons, Contains.Item("downstream-branches"));
    }
}
