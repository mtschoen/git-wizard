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

    [Test]
    public void FromReport_EmptyReport_HasNoRepositoriesAndNoAttention()
    {
        var summary = GitWizardSummary.FromReport(new GitWizardReport());

        Assert.That(summary.TotalRepositories, Is.EqualTo(0));
        Assert.That(summary.NeedingAttention, Is.Empty);
    }

    [Test]
    public void FromReport_DirtyRepo_CountsItAndRecordsTheReasons()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        File.WriteAllText(Path.Combine(fixture.Path, "uncommitted.txt"), "dirty");
        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        var report = new GitWizardReport();
        report.Repositories[fixture.Path] = repository;

        var summary = GitWizardSummary.FromReport(report);

        Assert.That(summary.Dirty, Is.EqualTo(1));
        // A throwaway local repo has no remote, so every commit also counts as unpushed.
        Assert.That(summary.Unpushed, Is.EqualTo(1));
        Assert.That(summary.NeedingAttention, Has.Count.EqualTo(1));
        Assert.That(summary.NeedingAttention[0].Path, Is.EqualTo(fixture.Path));
        Assert.That(summary.NeedingAttention[0].Reasons, Does.Contain("dirty"));
        Assert.That(summary.NeedingAttention[0].Reasons, Does.Contain("unpushed"));
    }

    [Test]
    public void FromReport_StaleRepo_CountsItAsStale()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit(DateTimeOffset.Now.AddDays(-60));
        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        var report = new GitWizardReport();
        report.Repositories[fixture.Path] = repository;

        var summary = GitWizardSummary.FromReport(report);

        Assert.That(summary.Stale, Is.EqualTo(1));
    }
}
