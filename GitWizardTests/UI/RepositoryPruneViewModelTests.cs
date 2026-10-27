using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

/// <summary>
/// The merged-detection surface of the repository list: the Stashes / Downstream / Local Only
/// filters, the Prunable and Stale Branches sorts, the row badge and the Clean flow.
/// </summary>
[TestFixture]
public class RepositoryPruneViewModelTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static RepositoryNodeViewModel Node(GitWizardRepository repository) => new(repository);

    static List<string> SortedPaths(SortMode mode, params GitWizardRepository[] repositories)
    {
        var viewModel = TestViewModels.Create();
        foreach (var repository in repositories)
        {
            viewModel.RepositoryList.AddRepository(repository, streaming: false);
        }

        viewModel.RepositoryList.SetSortMode(mode);
        return viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory).ToList();
    }

    [Test]
    public void StashesFilter_MatchesRepositoriesWithAnyStash()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Node(new GitWizardRepository("/a") { StashCount = 1 }).MatchesFilter(FilterType.Stashes), Is.True);
            Assert.That(Node(new GitWizardRepository("/b")).MatchesFilter(FilterType.Stashes), Is.False);
        }
    }

    [Test]
    public void DownstreamFilter_MatchesContentMergedBranch()
    {
        var repository = new GitWizardRepository("/a")
        {
            Branches = [new BranchInfo { Name = "squashed", IsMerged = true, MergedBy = MergedBy.Content }]
        };

        Assert.That(Node(repository).MatchesFilter(FilterType.DownstreamBranches), Is.True);
    }

    [Test]
    public void LocalOnlyFilter_IgnoresRepositoriesWhoseUnpushedBranchesAreAllMerged()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();
        scenario.NewBranch("merged-local");
        scenario.Commit("merged.txt", "merged", "merged work");
        scenario.Checkout("main");
        scenario.Git("merge", "-q", "--squash", "merged-local");
        scenario.Write("extra.txt", "fixup");
        scenario.Git("add", "extra.txt");
        scenario.Git("commit", "-q", "-m", "squash with fixup");
        scenario.Git("push", "-q", "origin", "main");
        var repository = new GitWizardRepository(scenario.Path);
        repository.Refresh();
        var node = Node(repository);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.LocalOnlyCommits, Is.True, "precondition: merged-local's commit is on no remote");
            Assert.That(node.MatchesFilter(FilterType.LocalOnlyCommits), Is.False);
        }

        scenario.NewBranch("unmerged-local");
        scenario.Commit("unmerged.txt", "unmerged", "unmerged work");
        repository.Refresh();

        Assert.That(node.MatchesFilter(FilterType.LocalOnlyCommits), Is.True);
    }

    [Test]
    public void PrunableSort_OrdersByMergedBranchesPlusMergedStashesDescending()
    {
        var none = new GitWizardRepository("/none");
        var stashes = new GitWizardRepository("/stashes") { StashCount = 3, MergedStashCount = 2 };
        var mixed = new GitWizardRepository("/mixed")
        {
            MergedStashCount = 1,
            Branches = [new BranchInfo { Name = "a", IsMerged = true }, new BranchInfo { Name = "b", IsMerged = true }]
        };

        Assert.That(SortedPaths(SortMode.Prunable, none, stashes, mixed), Is.EqualTo(["/mixed", "/stashes", "/none"]));
    }

    [Test]
    public void StaleBranchesSort_PutsOldestLocalOnlyBranchFirstAndRepositoriesWithoutOneLast()
    {
        var none = new GitWizardRepository("/none");
        var recent = new GitWizardRepository("/recent") { OldestLocalOnlyBranchDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero) };
        var old = new GitWizardRepository("/old") { OldestLocalOnlyBranchDate = new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero) };

        Assert.That(SortedPaths(SortMode.StaleBranches, none, recent, old), Is.EqualTo(["/old", "/recent", "/none"]));
    }

    [Test]
    public void PruneBadge_JoinsNonZeroCountsAndHidesWhenAllZero()
    {
        var busy = new GitWizardRepository("/busy")
        {
            StashCount = 2,
            MergedStashCount = 1,
            LocalOnlyBranchCount = 1,
            Branches = Enumerable.Range(0, 3).Select(i => new BranchInfo { Name = $"m{i}", IsMerged = true }).ToList()
        };
        var single = new GitWizardRepository("/single") { StashCount = 1 };
        var clean = Node(new GitWizardRepository("/clean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Node(busy).PruneBadgeText, Is.EqualTo("3 merged · 2 stashes · 1 local-only"));
            Assert.That(Node(busy).PruneBadgeTooltip, Does.Contain("1 already on the base, safe to drop"));
            Assert.That(Node(single).PruneBadgeText, Is.EqualTo("1 stash"));
            Assert.That(clean.PruneBadgeText, Is.Empty);
            Assert.That(clean.HasPruneBadge, Is.False);
            Assert.That(RepositoryNodeViewModel.CreateGroupHeader("C:\\").HasPruneBadge, Is.False);
        }
    }

    [Test]
    public void PruneBadge_RaisesPropertyChangedOnUpdate()
    {
        var node = Node(new GitWizardRepository("/a"));
        var changed = new List<string?>();
        node.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        node.Repository.StashCount = 1;
        node.UpdateDisplayText();

        Assert.That(changed, Does.Contain(nameof(RepositoryNodeViewModel.PruneBadgeText))
            .And.Contain(nameof(RepositoryNodeViewModel.HasPruneBadge)));
    }

    /// <summary>
    /// A squash-merged branch (only the expensive squash tier proves it), an ancestor-merged
    /// branch, an unmerged branch and a merged stash.
    /// </summary>
    static GitScenarioRepo CleanScenario()
    {
        var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("squashed");
        scenario.Commit("a.txt", "a", "a");
        scenario.Commit("b.txt", "b", "b");
        scenario.Checkout("main");
        scenario.SquashMerge("squashed");
        scenario.Commit("a.txt", "a, edited after the squash", "later edit");
        scenario.Git("branch", "ancestor");
        scenario.NewBranch("unmerged");
        scenario.Commit("unmerged.txt", "unmerged", "unmerged");
        scenario.Checkout("main");
        scenario.Write("base.txt", "landed edit");
        scenario.Git("stash", "push", "-q", "-m", "landed");
        scenario.Commit("base.txt", "landed edit", "land the stash");
        return scenario;
    }

    static RepositoryNodeViewModel RefreshedNode(GitScenarioRepo scenario)
    {
        var repository = new GitWizardRepository(scenario.Path);
        repository.Refresh();
        return Node(repository);
    }

    [Test]
    public async Task Clean_Confirmed_DeletesEveryListedCandidateIncludingSquashMerges()
    {
        using var scenario = CleanScenario();
        var stashSha = scenario.StashShas().Single();
        var node = RefreshedNode(scenario);
        var dialogs = new StubUserDialogs { NextConfirmResult = true };
        var logged = new List<string?>();
        var previousLogMethod = GitWizardLog.LogMethod;
        var previousSilentMode = GitWizardLog.SilentMode;
        GitWizardLog.SilentMode = false;
        GitWizardLog.LogMethod = logged.Add;
        try
        {
            await using var viewModel = TestViewModels.Create(dialogs: dialogs);

            await viewModel.Commands.CleanDownstreamBranchesAsync(node);

            var confirm = dialogs.ConfirmCalls.Single().Message;
            var quotedPath = Pruner.ShellQuote(scenario.Path);
            var logText = string.Join("\n", logged);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(confirm, Does.Contain("branch squashed (squash)"));
                Assert.That(confirm, Does.Contain("branch ancestor (ancestor)"));
                Assert.That(confirm, Does.Contain("stash stash@{0} (content)"));
                Assert.That(confirm, Does.Not.Contain("unmerged"));
                Assert.That(confirm, Does.Contain("2 other branch(es)/stash(es) skipped"), "unmerged and the checked-out main");
                Assert.That(scenario.HasBranch("squashed"), Is.False);
                Assert.That(scenario.HasBranch("ancestor"), Is.False);
                Assert.That(scenario.HasBranch("unmerged"), Is.True);
                Assert.That(scenario.StashShas(), Is.EqualTo([stashSha]));
                Assert.That(dialogs.AlertCalls.Single().Message, Does.StartWith("Deleted 2 item(s)"));
                Assert.That(node.Repository.StashCount, Is.EqualTo(1), "The stash remains in place and the repository is refreshed.");
                Assert.That(logText, Does.Contain($"git -C {quotedPath} stash list --format='%H %gd' | grep '^{stashSha} '")
                    .And.Contain($"git -C {quotedPath} stash drop stash@{{0}}"));
            }
        }
        finally
        {
            GitWizardLog.LogMethod = previousLogMethod;
            GitWizardLog.SilentMode = previousSilentMode;
        }
    }

    [Test]
    public async Task Clean_Cancelled_DeletesNothing()
    {
        using var scenario = CleanScenario();
        var node = RefreshedNode(scenario);
        var dialogs = new StubUserDialogs { NextConfirmResult = false };
        await using var viewModel = TestViewModels.Create(dialogs: dialogs);

        await viewModel.Commands.CleanDownstreamBranchesAsync(node);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dialogs.ConfirmCalls, Has.Count.EqualTo(1));
            Assert.That(dialogs.AlertCalls, Is.Empty);
            Assert.That(scenario.HasBranch("squashed"), Is.True);
            Assert.That(scenario.HasBranch("ancestor"), Is.True);
            Assert.That(scenario.StashShas(), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task Clean_InvalidPath_ShowsErrorWithoutConfirming()
    {
        var dialogs = new StubUserDialogs();
        var viewModel = TestViewModels.Create(dialogs: dialogs);

        await viewModel.Commands.CleanDownstreamBranchesAsync(Node(new GitWizardRepository("/no/such/directory/xyz")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dialogs.ConfirmCalls, Is.Empty);
            Assert.That(dialogs.AlertCalls.Single().Message, Is.EqualTo("Invalid repository path"));
        }
    }

    [Test]
    public void Pruner_OnlyShas_SkipsUnconfirmedCandidatesWithoutDeleting()
    {
        using var scenario = CleanScenario();
        var ancestorSha = scenario.Sha("ancestor");

        var report = Pruner.Run([scenario.Path], new PruneOptions(Apply: true, OnlyShas: new HashSet<string> { ancestorSha }), TextWriter.Null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Candidates.Single(c => c.Name == "ancestor").Action, Is.EqualTo(PruneCandidate.Deleted));
            Assert.That(report.Candidates.Single(c => c.Name == "squashed").SkipReason, Is.EqualTo(PruneSkipReasons.NotConfirmed));
            Assert.That(report.Candidates.Single(c => c.Kind == PruneCandidate.StashKind).SkipReason, Is.EqualTo(PruneSkipReasons.NotConfirmed));
            Assert.That(scenario.HasBranch("squashed"), Is.True);
            Assert.That(scenario.StashShas(), Has.Count.EqualTo(1));
        }
    }
}
