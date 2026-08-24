using System.Text.Json;
using GitWizard;

namespace GitWizardTests;

[TestFixture]
public class BranchInventoryReportTests
{
    [Test]
    public void Scan_ReportsLocalBranchesAndDivergence()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        repo.CommitOnNewBranch("feature-1", "feature1.txt");

        var report = BranchInventoryReport.Scan([repo.Path]);

        Assert.That(report.Repositories, Has.Count.EqualTo(1));
        var repoItem = report.Repositories[0];
        Assert.That(repoItem.Branches, Has.Count.EqualTo(1));

        var branch = repoItem.Branches[0];
        Assert.That(branch.Name, Is.EqualTo("feature-1"));
        Assert.That(branch.IsMerged, Is.False);
        Assert.That(branch.AheadOfBase, Is.EqualTo(1));
        Assert.That(branch.BehindBase, Is.EqualTo(0));
        Assert.That(branch.HasUpstream, Is.False);
        Assert.That(branch.UniqueCommitCount, Is.EqualTo(1));
    }

    [Test]
    public void Scan_NotMergedFilter_OmitsMergedBranches()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        repo.CommitOnNewBranch("feature-merged", "merged.txt");
        repo.MergeBranchNoFastForward("feature-merged");

        // Scan with notMergedOnly = true
        var report = BranchInventoryReport.Scan([repo.Path], notMergedOnly: true);

        // All local branches are merged into base, so repository has no branches to report
        Assert.That(report.Repositories, Is.Empty);

        // Scan with notMergedOnly = false
        var fullReport = BranchInventoryReport.Scan([repo.Path], notMergedOnly: false);
        Assert.That(fullReport.Repositories, Has.Count.EqualTo(1));
        Assert.That(fullReport.Repositories[0].Branches[0].IsMerged, Is.True);
        Assert.That(fullReport.Repositories[0].Branches[0].UniqueCommitCount, Is.EqualTo(0));
    }

    [Test]
    public void Scan_PushedBranch_HasZeroUniqueCommits()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        repo.AddOriginRemoteAndPush();

        // New branch created and pushed to origin
        repo.CommitOnNewBranch("feature-pushed", "pushed.txt");
        // Push feature-pushed branch
        using (var libRepo = new LibGit2Sharp.Repository(repo.Path))
        {
            var branch = libRepo.Branches["feature-pushed"];
            var origin = libRepo.Network.Remotes["origin"];
            libRepo.Branches.Update(branch, b => b.Remote = origin.Name, b => b.UpstreamBranch = branch.CanonicalName);
        }

        // Push via git command
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = repo.Path,
            Arguments = "push -u origin feature-pushed",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = System.Diagnostics.Process.Start(startInfo)!;
        proc.WaitForExit();

        var report = BranchInventoryReport.Scan([repo.Path]);

        Assert.That(report.Repositories, Has.Count.EqualTo(1));
        var branchInfo = report.Repositories[0].Branches.FirstOrDefault(b => b.Name == "feature-pushed");
        Assert.That(branchInfo, Is.Not.Null);
        Assert.That(branchInfo!.HasUpstream, Is.True);
        Assert.That(branchInfo.AheadOfUpstream, Is.EqualTo(0));
        Assert.That(branchInfo.UniqueCommitCount, Is.EqualTo(0), "Pushed commits are on remote, so unique commits should be 0");
    }

    [Test]
    public void Scan_SerializesToJsonWithSchemaVersion()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        repo.CommitOnNewBranch("feature-a", "a.txt");

        var report = BranchInventoryReport.Scan([repo.Path], baseBranchName: "main");

        var json = JsonSerializer.Serialize(report);
        Assert.That(json, Does.Contain("\"SchemaVersion\":\"1.0\""));
        Assert.That(json, Does.Contain("\"BaseBranch\":\"main\""));
        Assert.That(json, Does.Contain("feature-a"));
    }
}
