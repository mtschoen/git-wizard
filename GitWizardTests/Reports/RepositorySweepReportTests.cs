using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

public class RepositorySweepReportTests
{
    [Test]
    public void Scan_CleanPushedRepository_HasNoFindings()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();

        var report = RepositorySweepReport.Scan([fixture.Path]);

        var item = report.Repositories.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.SchemaVersion, Is.EqualTo("1.0"));
            Assert.That(item.Path, Is.EqualTo(fixture.Path));
            Assert.That(item.DirtyTrackedFiles, Is.Empty);
            Assert.That(item.UnpushedBranches, Is.Empty);
            Assert.That(item.StashCount, Is.Zero);
            Assert.That(item.Error, Is.Null);
        }
    }

    [Test]
    public void Scan_DirtyRepository_IncludesTrackedAndStagedButNotUntrackedFiles()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.ModifyTrackedFile("README.md");
        fixture.AddUntrackedFile("scratch.txt");
        fixture.AddStagedFile("added.txt");

        var item = RepositorySweepReport.Scan([fixture.Path]).Repositories.Single();

        Assert.That(item.DirtyTrackedFiles, Is.EqualTo(["README.md", "added.txt"]));
    }

    [Test]
    public void Scan_LocalBranches_CountsOnlyCommitsAbsentFromEveryRemote()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.AddBranchAtHead("remote-known");
        fixture.CommitOnNewBranch("feature/local", "feature.txt");
        fixture.AppendCommit("main-local.txt");
        using var repository = new Repository(fixture.Path);
        var defaultBranchName = repository.Head.FriendlyName;

        var item = RepositorySweepReport.Scan([fixture.Path]).Repositories.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.UnpushedBranches.Select(branch => branch.Name),
                    Is.EqualTo(new[] { "feature/local", defaultBranchName }.Order(StringComparer.Ordinal)));
            Assert.That(item.UnpushedBranches, Has.All.Property(nameof(UnpushedBranchInfo.CommitCount)).EqualTo(1));
        }
        Assert.That(item.UnpushedBranches.Select(branch => branch.Name), Does.Not.Contain("remote-known"));
    }

    [Test]
    public void Scan_RepositoryWithStash_ReportsStashCount()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.ModifyTrackedFile("README.md");
        fixture.StashTrackedChanges();

        var item = RepositorySweepReport.Scan([fixture.Path]).Repositories.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.DirtyTrackedFiles, Is.Empty);
            Assert.That(item.StashCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void Scan_MultiplePaths_SortsRecordsAndIsolatesInvalidRepositoryError()
    {
        using var first = TempRepoFixture.CreateWithInitialCommit();
        using var second = TempRepoFixture.CreateWithInitialCommit();
        using var invalidDir = new TempDirectory("gw-missing-", create: false);
        var invalid = invalidDir.Path;
        var paths = new[] { second.Path, invalid, first.Path };

        var report = RepositorySweepReport.Scan(paths);

        Assert.That(report.Repositories.Select(item => item.Path),
            Is.EqualTo(paths.Order(StringComparer.Ordinal)));
        var invalidItem = report.Repositories.Single(item => item.Path == invalid);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(invalidItem.Error, Is.Not.Empty);
            Assert.That(invalidItem.DirtyTrackedFiles, Is.Empty);
            Assert.That(invalidItem.UnpushedBranches, Is.Empty);
            Assert.That(invalidItem.StashCount, Is.Zero);
        }
    }
}
