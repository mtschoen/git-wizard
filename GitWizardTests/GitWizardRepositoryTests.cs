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
    public void BranchInfo_RoundTripsJson()
    {
        var branch = new BranchInfo
        {
            Name = "feature/x",
            IsMerged = false,
            MergedInto = null,
            AheadOfDefault = 4,
            BehindDefault = 1,
            LastCommitDate = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            HasUpstream = true
        };

        var json = JsonSerializer.Serialize(branch);
        var deserialized = JsonSerializer.Deserialize<BranchInfo>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.Name, Is.EqualTo("feature/x"));
        Assert.That(deserialized.AheadOfDefault, Is.EqualTo(4));
        Assert.That(deserialized.BehindDefault, Is.EqualTo(1));
        Assert.That(deserialized.IsMerged, Is.False);
        Assert.That(deserialized.HasUpstream, Is.True);
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
    public void Refresh_FindsMatchingBranchWhenDetachedAtLocalBranchTip()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        // Detach HEAD at the current tip; capture the default branch name to
        // assert against (varies by git's init.defaultBranch: "master" or "main").
        string defaultBranchName;
        using (var repo = new Repository(fixture.Path))
        {
            defaultBranchName = repo.Head.FriendlyName;
            var tip = repo.Head.Tip!;
            Commands.Checkout(repo, tip.Sha);
        }

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.IsDetachedHead, Is.True);
        Assert.That(repository.MatchingBranchName, Is.EqualTo(defaultBranchName));
    }

    [Test]
    public void Refresh_MatchingBranchNameIsNullWhenNotDetached()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.IsDetachedHead, Is.False);
        Assert.That(repository.MatchingBranchName, Is.Null.Or.Empty);
    }

    [Test]
    public void Refresh_MatchingBranchNameIsNullWhenNoLocalBranchAtTip()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        // Detach HEAD at a commit that is not the tip of any local branch.
        // Remove whichever default branch git init produced ("master" or "main").
        using (var repo = new Repository(fixture.Path))
        {
            var defaultBranchName = repo.Head.FriendlyName;
            var tip = repo.Head.Tip!;
            Commands.Checkout(repo, tip.Sha);
            repo.Branches.Remove(defaultBranchName);
        }

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.IsDetachedHead, Is.True);
        Assert.That(repository.MatchingBranchName, Is.Null.Or.Empty);
    }

    [Test]
    public void Refresh_DetectsUnmergedBranch()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("feature/ahead", "ahead.txt");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.Branches, Is.Not.Null);
        var feature = repository.Branches!.SingleOrDefault(b => b.Name == "feature/ahead");
        Assert.That(feature, Is.Not.Null);
        Assert.That(feature!.IsMerged, Is.False);
        Assert.That(feature.AheadOfDefault, Is.GreaterThan(0));
        Assert.That(feature.MergedInto, Is.Null);
    }

    [Test]
    public void Refresh_DetectsMergedButBehindBranchAsSafeToDelete()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        using (var repo = new Repository(fixture.Path))
            repo.Branches.Add("old/merged", repo.Head.Tip);
        fixture.AppendCommit("advance.txt");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.Branches, Is.Not.Null);
        var merged = repository.Branches!.SingleOrDefault(b => b.Name == "old/merged");
        Assert.That(merged, Is.Not.Null);
        Assert.That(merged!.IsMerged, Is.True);
        Assert.That(merged.AheadOfDefault, Is.EqualTo(0));
        Assert.That(merged.BehindDefault, Is.GreaterThan(0));
        Assert.That(merged.MergedInto, Is.AnyOf("main", "master"));
    }

    [Test]
    public void Refresh_ExcludesDefaultBranchAndBranchesAtDefaultTip()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        using (var repo = new Repository(fixture.Path))
            repo.Branches.Add("samepoint", repo.Head.Tip);

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.Branches, Is.Null);
        Assert.That(repository.DefaultBranch, Is.AnyOf("main", "master"));
    }

    [Test]
    public void Refresh_ClearsBranchesWhenNoneRemainActionable()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("feature/ahead", "ahead.txt");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        Assert.That(repository.Branches, Is.Not.Null, "precondition: unmerged branch detected");

        // Remove the unmerged branch; a re-refresh must not retain the stale list.
        using (var repo = new Repository(fixture.Path))
            repo.Branches.Remove("feature/ahead");
        repository.Refresh();

        Assert.That(repository.Branches, Is.Null);
    }

    [Test]
    public void Refresh_AllBranchesIncludesDefaultAndBoringBranches()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using (var repo = new Repository(fixture.Path))
            repo.Branches.Add("samepoint", repo.Head.Tip);

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh(allBranches: true);

        Assert.That(repository.Branches, Is.Not.Null);
        var names = repository.Branches!.Select(b => b.Name).ToList();
        Assert.That(names, Does.Contain("samepoint"));
        // default branch is included in full mode but must NOT claim MergedInto=itself
        var def = repository.Branches!.SingleOrDefault(b => b.Name == repository.DefaultBranch);
        Assert.That(def, Is.Not.Null);
        Assert.That(def!.MergedInto, Is.Null);
    }

    [Test]
    public void Refresh_FlagsSubmoduleDeclaredButMissingFromIndex()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOrphanedGitmodulesEntry("libfoo", "external/libfoo");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.HasSubmoduleIssues, Is.True);
        Assert.That(repository.SubmoduleHealth, Does.ContainKey("external/libfoo"));
        Assert.That(repository.SubmoduleHealth["external/libfoo"].Status,
            Is.EqualTo(SubmoduleHealthStatus.MissingFromIndex));
        Assert.That(repository.SubmoduleHealth["external/libfoo"].Issues, Is.Not.Empty);
    }

    [Test]
    public void Refresh_FlagsGitlinkMissingFromGitmodules()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddGitlinkWithoutGitmodules("orphan/libbar");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.HasSubmoduleIssues, Is.True);
        Assert.That(repository.SubmoduleHealth["orphan/libbar"].Status,
            Is.EqualTo(SubmoduleHealthStatus.MissingFromGitmodules));
    }

    [Test]
    public void Refresh_FlagsUninitializedSubmodule()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddUninitializedSubmodule("external/libfoo");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.HasSubmoduleIssues, Is.True);
        Assert.That(repository.SubmoduleHealth["external/libfoo"].Status,
            Is.EqualTo(SubmoduleHealthStatus.Uninitialized));
    }

    [Test]
    public void Refresh_FlagsSubmoduleAtWrongRef()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddSubmoduleAtWrongRef("external/libfoo");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.HasSubmoduleIssues, Is.True);
        var health = repository.SubmoduleHealth["external/libfoo"];
        Assert.That(health.Status, Is.EqualTo(SubmoduleHealthStatus.WrongRef));
        Assert.That(health.ExpectedCommitSha, Is.Not.Null);
        Assert.That(health.ActualCommitSha, Is.Not.Null);
        Assert.That(health.ActualCommitSha, Is.Not.EqualTo(health.ExpectedCommitSha));
    }

    [Test]
    public void Refresh_HealthyRepoReportsNoSubmoduleIssues()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.HasSubmoduleIssues, Is.False);
        Assert.That(repository.SubmoduleHealth, Is.Empty);
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
