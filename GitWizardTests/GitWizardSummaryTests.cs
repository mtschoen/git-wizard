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
        Assert.That(summary.SchemaVersion, Is.EqualTo("2.0"));
    }

    [Test]
    public void FromReport_CountsMergedBranches()
    {
        GitWizardLog.SilentMode = true;
        var report = new GitWizardReport();

        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var repository = new GitWizardRepository(fixture.Path);
        repository.Branches = new List<BranchInfo>
        {
            new BranchInfo { Name = "feature/x", IsMerged = true, MergedInto = "main" }
        };
        report.Repositories[fixture.Path] = repository;

        var summary = GitWizardSummary.FromReport(report);

        Assert.That(summary.TotalRepositories, Is.EqualTo(1));
        Assert.That(summary.MergedBranches, Is.EqualTo(1));
        Assert.That(summary.NeedingAttention, Has.Count.EqualTo(1));
        Assert.That(summary.NeedingAttention[0].Reasons, Contains.Item("merged-branches"));
    }
}
