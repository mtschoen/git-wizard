using System.Text.Json;
using GitWizard;
using LibGit2Sharp;

namespace GitWizardTests;

public class GitWizardRepositoryTests
{
    [Test]
    public void GitWizardCommitInfo_RoundTripsJson()
    {
        var commit = new GitWizardCommitInfo
        {
            Hash = "abc1234",
            Message = "Fix the thing",
            Date = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero),
            AuthorEmail = "dev@example.com"
        };

        var json = JsonSerializer.Serialize(commit);
        var deserialized = JsonSerializer.Deserialize<GitWizardCommitInfo>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.Hash, Is.EqualTo("abc1234"));
        Assert.That(deserialized.Message, Is.EqualTo("Fix the thing"));
        Assert.That(deserialized.AuthorEmail, Is.EqualTo("dev@example.com"));
    }

    [Test]
    public void Refresh_PopulatesRecentCommits()
    {
        GitWizardLog.SilentMode = true;
        var repoPath = FindRepoRoot();
        var repository = new GitWizardRepository(repoPath);
        repository.Refresh();

        Assert.That(repository.RecentCommits, Is.Not.Null);
        Assert.That(repository.RecentCommits, Has.Count.GreaterThan(0));
        Assert.That(repository.RecentCommits!.Count, Is.LessThanOrEqualTo(10));

        var first = repository.RecentCommits[0];
        Assert.That(first.Hash, Has.Length.EqualTo(7));
        Assert.That(first.Message, Is.Not.Empty);
        Assert.That(first.AuthorEmail, Is.Not.Empty);
    }

    [Test]
    public void Refresh_PopulatesDaysSinceLastCommit()
    {
        GitWizardLog.SilentMode = true;
        var repoPath = FindRepoRoot();
        var repository = new GitWizardRepository(repoPath);
        repository.Refresh();

        Assert.That(repository.DaysSinceLastCommit, Is.Not.Null);
        Assert.That(repository.DaysSinceLastCommit, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Refresh_CountsUntrackedFilesInPendingChanges()
    {
        // Regression: a repo whose only changes are untracked files used to
        // report HasPendingChanges=true with NumberOfPendingChanges=0. Both
        // must agree so callers never see "dirty but zero changes".
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        File.WriteAllText(Path.Combine(fixture.Path, "untracked.txt"), "new");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.HasPendingChanges, Is.True);
        Assert.That(repository.NumberOfPendingChanges, Is.GreaterThan(0));
    }

    [Test]
    public void Refresh_CountsLocalCommitsOnUntrackedBranch()
    {
        // Regression: LocalOnlyCommits used to be a bare bool. Consumers need
        // an actual count so they can show "3 unpushed" instead of "yes/no".
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");
        fixture.AppendCommit("third.txt");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.LocalOnlyCommits, Is.True);
        Assert.That(repository.LocalCommitCount, Is.EqualTo(3));
    }

    [Test]
    public void Refresh_DetectsDownstreamBranches()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        using var repo = new LibGit2Sharp.Repository(fixture.Path);
        repo.Branches.Add("feature/test", repo.Head.Tip);

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.DownstreamBranches, Is.Not.Null);
        Assert.That(repository.DownstreamBranches, Has.Count.EqualTo(1));
        Assert.That(repository.DownstreamBranches![0].Name, Is.EqualTo("feature/test"));
        Assert.That(repository.DownstreamBranches[0].MergedInto, Is.AnyOf("main", "master"));
    }

    [Test]
    public void Refresh_DetectsMultipleDownstreamBranches()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        using var repo = new LibGit2Sharp.Repository(fixture.Path);
        repo.Branches.Add("feature/a", repo.Head.Tip);
        repo.Branches.Add("feature/b", repo.Head.Tip);
        repo.Branches.Add("bugfix/c", repo.Head.Tip);

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.DownstreamBranches, Is.Not.Null);
        Assert.That(repository.DownstreamBranches, Has.Count.EqualTo(3));
    }

    [Test]
    public void Refresh_DoesNotListCurrentBranchAsDownstream()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        using var repo = new LibGit2Sharp.Repository(fixture.Path);
        repo.Branches.Add("feature/skip-me", repo.Head.Tip);

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.DownstreamBranches, Is.Not.Null);
        Assert.That(repository.DownstreamBranches, Has.Count.EqualTo(1));
        Assert.That(repository.DownstreamBranches![0].Name, Is.EqualTo("feature/skip-me"));
        Assert.That(repository.DownstreamBranches![0].Name, Is.Not.EqualTo("main"));
        Assert.That(repository.DownstreamBranches.Any(b => b.Name == "main"), Is.False);
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
