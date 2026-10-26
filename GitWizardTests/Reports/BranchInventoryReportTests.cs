using System.Text.Json;
using GitWizard;
using LibGit2Sharp;

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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(branch.Name, Is.EqualTo("feature-1"));
            Assert.That(branch.IsMerged, Is.False);
            Assert.That(branch.AheadOfBase, Is.EqualTo(1));
            Assert.That(branch.BehindBase, Is.Zero);
            Assert.That(branch.HasUpstream, Is.False);
            Assert.That(branch.UniqueCommitCount, Is.EqualTo(1));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fullReport.Repositories[0].Branches[0].IsMerged, Is.True);
            Assert.That(fullReport.Repositories[0].Branches[0].UniqueCommitCount, Is.Zero);
        }
    }

    [Test]
    public void Scan_PushedBranch_HasZeroUniqueCommits()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        repo.AddOriginRemoteAndPush();

        // New branch created and pushed to origin
        repo.CommitOnNewBranch("feature-pushed", "pushed.txt");
        // Push feature-pushed branch
        using (var libRepo = new Repository(repo.Path))
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(branchInfo!.HasUpstream, Is.True);
            Assert.That(branchInfo.AheadOfUpstream, Is.Zero);
            Assert.That(branchInfo.UniqueCommitCount, Is.Zero, "Pushed commits are on remote, so unique commits should be 0");
        }
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

    [TestCase("https://example.invalid/team/repository.git")]
    [TestCase("git@example.invalid:team/repository.git")]
    [TestCase("missing-remote")]
    [TestCase("Origin")]
    [TestCase(".")]
    public void Scan_UnconfiguredRemote_ReportsBranchAsUntracked(string remoteName)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("a-affected", "affected.txt");
        fixture.CommitOnNewBranch("z-healthy", "healthy.txt");
        string baseBranchName;
        using (var repository = new Repository(fixture.Path))
        {
            baseBranchName = repository.Head.FriendlyName;
            repository.Network.Remotes.Add("origin", "https://example.invalid/unused.git");
            repository.Config.Set("branch.a-affected.remote", remoteName);
            repository.Config.Set("branch.a-affected.merge", repository.Head.CanonicalName);
        }

        var report = BranchInventoryReport.Scan([fixture.Path], baseBranchName);

        Assert.That(report.Repositories, Has.Count.EqualTo(1));
        var item = report.Repositories[0];
        Assert.That(item.Error, Is.Null);
        var branchNames = item.Branches.Select(branch => branch.Name);
        Assert.That(branchNames, Is.EqualTo(["a-affected", "z-healthy"]));
        var affected = item.Branches[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(affected.HasUpstream, Is.False);
            Assert.That(affected.UpstreamBranch, Is.Null);
            Assert.That(affected.AheadOfUpstream, Is.Zero);
            Assert.That(affected.BehindUpstream, Is.Zero);
            Assert.That(affected.AheadOfBase, Is.EqualTo(1));
            Assert.That(affected.BehindBase, Is.Zero);
            Assert.That(affected.IsMerged, Is.False);
            Assert.That(affected.UniqueCommitCount, Is.EqualTo(1));
            Assert.That(affected.LastCommitDate, Is.Not.Null);
        }
    }

    [TestCase("origin", true)]
    [TestCase("upstream", true)]
    [TestCase("origin", false)]
    public void Scan_ConfiguredRemote_PreservesTrackingResolution(
        string remoteName, bool hasTrackingReference)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("feature-tracked", "tracked.txt");
        fixture.CommitOnNewBranch("remote-work", "remote.txt");
        string baseBranchName;
        using (var repository = new Repository(fixture.Path))
        {
            baseBranchName = repository.Head.FriendlyName;
            repository.Network.Remotes.Add(remoteName, "https://example.invalid/unused.git");
            if (hasTrackingReference)
            {
                repository.Refs.Add($"refs/remotes/{remoteName}/feature-tracked",
                    repository.Branches["remote-work"].Tip.Id);
            }
            repository.Config.Set("branch.feature-tracked.remote", remoteName);
            repository.Config.Set("branch.feature-tracked.merge", "refs/heads/feature-tracked");
        }

        var report = BranchInventoryReport.Scan([fixture.Path], baseBranchName);

        Assert.That(report.Repositories, Has.Count.EqualTo(1));
        var item = report.Repositories[0];
        Assert.That(item.Error, Is.Null);
        var branch = item.Branches.Single(candidate => candidate.Name == "feature-tracked");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(branch.HasUpstream, Is.True);
            Assert.That(branch.UpstreamBranch, Is.EqualTo($"{remoteName}/feature-tracked"));
            Assert.That(branch.AheadOfUpstream, Is.EqualTo(hasTrackingReference ? 1 : 0));
            Assert.That(branch.BehindUpstream, Is.EqualTo(hasTrackingReference ? 1 : 0));
            Assert.That(branch.AheadOfBase, Is.EqualTo(1));
            Assert.That(branch.UniqueCommitCount, Is.EqualTo(1));
        }
    }
}
