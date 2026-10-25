using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// Covers the branch/worktree/checkout paths in
/// <see cref="GitWizardRepository"/> (the BranchesAndWorktrees partial): detached-HEAD branch
/// matching, worktree discovery, the full-inventory branch view, the deep-refresh index pass, and
/// <see cref="GitWizardRepository.CheckoutBranch"/>.
/// </summary>
// Repository branches tests exercise branch and worktree inventory across tracking configurations.
public class GitWizardRepositoryBranchesTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void CheckoutBranch_MovesWorkingTreeToBranchTip()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("feature", "feature.txt");
        var repo = new GitWizardRepository(fixture.Path);

        repo.CheckoutBranch("feature");

        using var library = new Repository(fixture.Path);
        var featureTip = library.Branches["feature"].Tip!.Sha;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(library.Head.Tip!.Sha, Is.EqualTo(featureTip),
                "Checkout must move HEAD to the named branch's tip commit.");
            Assert.That(File.Exists(Path.Combine(fixture.Path, "feature.txt")), Is.True,
                "The branch's file must be present in the working tree after checkout.");
        }
    }

    [Test]
    public void CheckoutBranch_ThrowsWhenWorkingDirectoryEmpty()
    {
        var repo = new GitWizardRepository(string.Empty);

        Assert.Throws<InvalidOperationException>(() => repo.CheckoutBranch("main"));
    }

    [Test]
    public void CheckoutBranch_ThrowsForNonExistentWorkingDirectory()
    {
        var repo = new GitWizardRepository("/nonexistent");
        Assert.Throws<RepositoryNotFoundException>(() => repo.CheckoutBranch("main"));
    }

    [Test]
    public void Refresh_DetachedHeadAtBranchTip_FindsMatchingBranch()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.DetachHead();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.IsDetachedHead, Is.True);
            Assert.That(repo.MatchingBranchName, Is.AnyOf("main", "master"),
                "A detached HEAD sitting on the default branch's tip must resolve to that branch.");
        }
    }

    [Test]
    public void Refresh_DetachedHeadOnUniqueCommit_LeavesMatchingBranchNull()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");
        fixture.DetachHead("HEAD~1");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.IsDetachedHead, Is.True);
            Assert.That(repo.MatchingBranchName, Is.Null,
                "No local branch points at this commit, so no match must be recorded.");
        }
    }

    [Test]
    public void Refresh_PopulatesWorktrees_AndNotifiesHandler()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var worktreePath = fixture.AddWorktree("wt-branch");
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new RecordingUpdateHandler();

        repo.Refresh(handler);

        Assert.That(repo.Worktrees, Is.Not.Null);
        Assert.That(repo.Worktrees!, Is.Not.Empty, "The linked worktree must be discovered.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.IsWorktree, Is.False, "The primary repository is not itself a worktree.");
            Assert.That(handler.WorktreesCreated, Is.Not.Empty,
                "OnWorktreeCreated must fire for the newly discovered worktree.");
        }

        var discovered = repo.Worktrees!.Values
            .Where(w => w != null)
            .Select(w => w!.WorkingDirectory!.TrimEnd('/', '\\'))
            .ToList();
        var expected = worktreePath.Replace('\\', '/').TrimEnd('/');
        Assert.That(discovered.Any(d => string.Equals(
            d.Replace('\\', '/'), expected, StringComparison.OrdinalIgnoreCase)), Is.True,
            $"Expected a worktree at {expected}; got [{string.Join(", ", discovered)}].");
    }

    [Test]
    public void Refresh_AllBranches_KeepsBranchAtDefaultTip()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddBranchAtHead("at-tip");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        Assert.That(repo.Branches!.Any(b => b.Name == "at-tip"), Is.True,
            "The full inventory must include a branch sitting exactly at the default tip.");
    }

    [Test]
    public void Refresh_ActionableBranches_DropsBranchAtDefaultTip()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddBranchAtHead("at-tip");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        var actionable = repo.Branches ?? [];
        Assert.That(actionable.Any(b => b.Name == "at-tip"), Is.False,
            "The actionable view must drop a branch sitting exactly at the default tip.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Refresh_DeepRefresh_RefreshesIndexWithoutError(bool modifyFile)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        if (modifyFile)
        {
            File.WriteAllText(Path.Combine(fixture.Path, "README.md"), "modified");
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(deepRefresh: true);

        Assert.That(repo.RefreshError, Is.Null);
        if (modifyFile)
        {
            Assert.That(repo.HasPendingChanges, Is.True);
        }
    }

    [Test]
    public void Refresh_FindsMatchingBranchWhenDetachedAtLocalBranchTip()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        string defaultBranchName;
        using (var repo = new Repository(fixture.Path))
        {
            defaultBranchName = repo.Head.FriendlyName;
            var tip = repo.Head.Tip!;
            Commands.Checkout(repo, tip.Sha);
        }

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.IsDetachedHead, Is.True);
            Assert.That(repository.MatchingBranchName, Is.EqualTo(defaultBranchName));
        }
    }

    [Test]
    public void Refresh_MatchingBranchNameIsNullWhenNotDetached()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.IsDetachedHead, Is.False);
            Assert.That(repository.MatchingBranchName, Is.Null.Or.Empty);
        }
    }

    [Test]
    public void Refresh_MatchingBranchNameIsNullWhenNoLocalBranchAtTip()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        using (var repo = new Repository(fixture.Path))
        {
            var defaultBranchName = repo.Head.FriendlyName;
            var tip = repo.Head.Tip!;
            Commands.Checkout(repo, tip.Sha);
            repo.Branches.Remove(defaultBranchName);
        }

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.IsDetachedHead, Is.True);
            Assert.That(repository.MatchingBranchName, Is.Null.Or.Empty);
        }
    }

    [Test]
    public void Refresh_ClearsMatchingBranchNameWhenDetachedHeadMovesToUnmatchedCommit()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("second.txt");

        string defaultBranchName;
        string firstCommitSha;
        using (var repo = new Repository(fixture.Path))
        {
            defaultBranchName = repo.Head.FriendlyName;
            firstCommitSha = repo.Head.Tip!.Parents.First().Sha;
            Commands.Checkout(repo, repo.Head.Tip!.Sha);
        }

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        Assert.That(repository.MatchingBranchName, Is.EqualTo(defaultBranchName));

        using (var repo = new Repository(fixture.Path))
        {
            Commands.Checkout(repo, firstCommitSha);
        }

        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.IsDetachedHead, Is.True);
            Assert.That(repository.MatchingBranchName, Is.Null);
        }
    }

    [Test]
    public void Refresh_DetectsUnmergedBranch()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("feature/ahead", "ahead.txt");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.Branches, Is.Not.Null);
        var feature = repository.Branches!.SingleOrDefault(b => b.Name == "feature/ahead");
        Assert.That(feature, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(feature!.IsMerged, Is.False);
            Assert.That(feature.AheadOfDefault, Is.GreaterThan(0));
            Assert.That(feature.MergedInto, Is.Null);
        }
    }

    [Test]
    public void Refresh_DetectsMergedButBehindBranchAsSafeToDelete()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        using (var repo = new Repository(fixture.Path))
        {
            repo.Branches.Add("old/merged", repo.Head.Tip);
        }

        fixture.AppendCommit("advance.txt");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.Branches, Is.Not.Null);
        var merged = repository.Branches!.SingleOrDefault(b => b.Name == "old/merged");
        Assert.That(merged, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged!.IsMerged, Is.True);
            Assert.That(merged.AheadOfDefault, Is.Zero);
            Assert.That(merged.BehindDefault, Is.GreaterThan(0));
            Assert.That(merged.MergedInto, Is.AnyOf("main", "master"));
        }
    }

    [Test]
    public void Refresh_ExcludesDefaultBranchAndBranchesAtDefaultTip()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        using (var repo = new Repository(fixture.Path))
        {
            repo.Branches.Add("samepoint", repo.Head.Tip);
        }

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.Branches, Is.Null);
            Assert.That(repository.DefaultBranch, Is.AnyOf("main", "master"));
        }
    }

    [Test]
    public void Refresh_ClearsBranchesWhenNoneRemainActionable()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("feature/ahead", "ahead.txt");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        Assert.That(repository.Branches, Is.Not.Null, "precondition: unmerged branch detected");

        using (var repo = new Repository(fixture.Path))
        {
            repo.Branches.Remove("feature/ahead");
        }

        repository.Refresh();

        Assert.That(repository.Branches, Is.Null);
    }

    [Test]
    public void Refresh_AllBranchesIncludesDefaultAndBoringBranches()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using (var repo = new Repository(fixture.Path))
        {
            repo.Branches.Add("samepoint", repo.Head.Tip);
        }

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh(allBranches: true);

        Assert.That(repository.Branches, Is.Not.Null);
        var names = repository.Branches!.ConvertAll(b => b.Name);
        Assert.That(names, Does.Contain("samepoint"));
        var def = repository.Branches!.SingleOrDefault(b => b.Name == repository.DefaultBranch);
        Assert.That(def, Is.Not.Null);
        Assert.That(def!.MergedInto, Is.Null);
    }

    [Test]
    public void Refresh_MultipleBranches_BothAheadAndBehind()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("diverged", "diverged.txt");
        fixture.AppendCommit("advance.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var diverged = repo.Branches!.FirstOrDefault(b => b.Name == "diverged");
        Assert.That(diverged, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(diverged!.AheadOfDefault, Is.GreaterThan(0));
            Assert.That(diverged.BehindDefault, Is.GreaterThan(0));
            Assert.That(diverged.IsMerged, Is.False);
        }
    }

    [Test]
    public void Refresh_BranchWithUpstream_HasUpstreamTrue()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.CommitOnNewBranch("tracked", "tracked.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var defaultBranch = repo.Branches!.FirstOrDefault(b => b.Name == repo.DefaultBranch);
        Assert.That(defaultBranch, Is.Not.Null);
        Assert.That(defaultBranch!.HasUpstream, Is.True);
    }

    [Test]
    public void Refresh_BranchWithNoUpstream_HasUpstreamFalse()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("no-upstream", "no-upstream.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var branch = repo.Branches!.FirstOrDefault(b => b.Name == "no-upstream");
        Assert.That(branch, Is.Not.Null);
        Assert.That(branch!.HasUpstream, Is.False);
    }

    [Test]
    public void Refresh_DetachedHead_NoDefaultBranch_DefaultBranchNull()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using (var libRepo = new Repository(fixture.Path))
        {
            var defaultName = libRepo.Head.FriendlyName;
            var tip = libRepo.Head.Tip!;
            Commands.Checkout(libRepo, tip.Sha);
            libRepo.Branches.Remove(defaultName);
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.IsDetachedHead, Is.True);
            Assert.That(repo.DefaultBranch, Is.Null);
        }
    }

    [Test]
    public void Refresh_WithDevelopBranch_FallsBackToDevelop()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using (var libRepo = new Repository(fixture.Path))
        {
            var defaultName = libRepo.Head.FriendlyName;
            libRepo.Branches.Add("develop", libRepo.Head.Tip);
            libRepo.Branches.Add("other", libRepo.Head.Tip);
            Commands.Checkout(libRepo, libRepo.Branches["develop"]);
            libRepo.Branches.Remove(defaultName);
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.DefaultBranch, Is.EqualTo("develop"));
    }

    [Test]
    public void Refresh_MultipleWorktrees_AllDiscovered()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddWorktree("wt-a");
        fixture.AddWorktree("wt-b");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.Worktrees, Is.Not.Null);
        Assert.That(repo.Worktrees!, Has.Count.EqualTo(2));
    }

    [Test]
    public void Refresh_DefaultBranch_ResolvesToMainOrMaster()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();

        Assert.That(repo.DefaultBranch, Is.AnyOf("main", "master"));
    }

    [Test]
    public void Refresh_BranchAheadOfDefault_ShowsAheadCount()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("feature-x", "feature-x.txt");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var feature = repo.Branches!.FirstOrDefault(b => b.Name == "feature-x");
        Assert.That(feature, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(feature!.AheadOfDefault, Is.GreaterThan(0));
            Assert.That(feature.IsMerged, Is.False);
        }
    }

    [Test]
    public void Refresh_BranchMergedIntoDefault_ShowsAsMerged()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("to-merge", "merge-me.txt");
        fixture.MergeBranchNoFastForward("to-merge");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var merged = repo.Branches!.FirstOrDefault(b => b.Name == "to-merge");
        Assert.That(merged, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged!.IsMerged, Is.True);
            Assert.That(merged.MergedInto, Is.Not.Null.And.Not.Empty);
            Assert.That(merged.AheadOfDefault, Is.Zero);
        }
    }

    [Test]
    public void Refresh_MultipleBranches_CountsDivergenceCorrectly()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("one-ahead", "one.txt");
        fixture.CommitOnNewBranch("two-ahead", "two-a.txt");
        using (var libRepo = new Repository(fixture.Path))
        {
            var branch = libRepo.Branches["two-ahead"];
            Commands.Checkout(libRepo, branch);
        }
        fixture.AppendCommit("two-b.txt");
        using (var libRepo = new Repository(fixture.Path))
        {
            var defaultBranch = libRepo.Branches["main"] ?? libRepo.Branches["master"];
            Commands.Checkout(libRepo, defaultBranch!);
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var oneAhead = repo.Branches!.FirstOrDefault(b => b.Name == "one-ahead");
        var twoAhead = repo.Branches!.FirstOrDefault(b => b.Name == "two-ahead");

        Assert.Multiple(() =>
        {
            Assert.That(oneAhead, Is.Not.Null);
            Assert.That(oneAhead!.AheadOfDefault, Is.EqualTo(1));
            Assert.That(twoAhead, Is.Not.Null);
            Assert.That(twoAhead!.AheadOfDefault, Is.EqualTo(2));
        });
    }

    [Test]
    public void Refresh_BranchBehindDefault_ShowsBehindCount()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddBranchAtHead("stale-branch");
        fixture.AppendCommit("advance.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var stale = repo.Branches!.FirstOrDefault(b => b.Name == "stale-branch");
        Assert.That(stale, Is.Not.Null);
        Assert.That(stale!.BehindDefault, Is.GreaterThan(0));
    }

    [Test]
    public void Refresh_BranchHasLastCommitDate()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("dated", "dated.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        Assert.That(repo.Branches, Is.Not.Null);
        var dated = repo.Branches!.FirstOrDefault(b => b.Name == "dated");
        Assert.That(dated, Is.Not.Null);
        Assert.That(dated!.LastCommitDate, Is.Not.Null);
    }

    [Test]
    public void Refresh_Worktree_DoesNotRecurseIntoWorktrees()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var worktreePath = fixture.AddWorktree("wt-test");

        var worktreeRepo = new GitWizardRepository(worktreePath);
        worktreeRepo.IsWorktree = true;
        worktreeRepo.Refresh();

        Assert.That(worktreeRepo.Worktrees, Is.Null);
    }

    [Test]
    public void Refresh_WithWorktree_ReRefresh_ReusesExistingWorktree()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddWorktree("wt-stable");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh();
        var firstWorktreeCount = repo.Worktrees?.Count ?? 0;

        repo.Refresh();
        var secondWorktreeCount = repo.Worktrees?.Count ?? 0;

        Assert.That(secondWorktreeCount, Is.EqualTo(firstWorktreeCount));
    }

    [Test]
    public void Refresh_DetachedAtFeatureBranch_FindsMatchingBranch()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("my-feature", "feature-file.txt");

        using (var libRepo = new Repository(fixture.Path))
        {
            var featureTip = libRepo.Branches["my-feature"].Tip;
            Commands.Checkout(libRepo, featureTip);
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.IsDetachedHead, Is.True);
            Assert.That(repo.MatchingBranchName, Is.EqualTo("my-feature"));
        }
    }

    [Test]
    public void IsWorktree_CanBeSet()
    {
        var repo = new GitWizardRepository("/some/path");
        repo.IsWorktree = true;
        Assert.That(repo.IsWorktree, Is.True);
    }

    [Test]
    public void RefreshWorktrees_UnknownState_LogsError()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var worktreePath = fixture.AddWorktree("wt-corrupt");

        if (Directory.Exists(worktreePath))
        {
            Directory.Delete(worktreePath, recursive: true);
            Directory.CreateDirectory(worktreePath);
        }

        var repo = new GitWizardRepository(fixture.Path);
        Assert.DoesNotThrow(() => repo.Refresh());
    }

}
