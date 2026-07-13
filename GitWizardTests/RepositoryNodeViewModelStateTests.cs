using GitWizard;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

/// <summary>
/// Drives <see cref="RepositoryNodeViewModel"/> from real repository state (built with
/// <see cref="TempRepoFixture"/>) to cover the "positive" branches the property-level tests leave
/// uncovered: pending/stale/detached/merged display decorations, the filter predicates returning
/// true, matching-branch checkout, and the submodule-issue tooltip.
/// </summary>
public class RepositoryNodeViewModelStateTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static RepositoryNodeViewModel RefreshedNode(TempRepoFixture fixture)
    {
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();
        return new RepositoryNodeViewModel(repo);
    }

    [Test]
    public void StatusColorHex_ReturnsDistinctColorForEachStatus()
    {
        var node = new RepositoryNodeViewModel(new GitWizardRepository("/x"));
        var colors = new Dictionary<RefreshStatus, string>();
        foreach (var status in Enum.GetValues<RefreshStatus>())
        {
            node.Status = status;
            colors[status] = node.StatusColorHex;
        }

        Assert.Multiple(() =>
        {
            Assert.That(colors[RefreshStatus.Success], Is.EqualTo("#28A745"));
            Assert.That(colors[RefreshStatus.Timeout], Is.EqualTo("#FFA500"));
            Assert.That(colors[RefreshStatus.Error], Is.EqualTo("#DC3545"));
            Assert.That(colors[RefreshStatus.SubmoduleIssue], Is.EqualTo("#8E44AD"));
            Assert.That(colors[RefreshStatus.Refreshing], Is.EqualTo("#808080"));
        });
    }

    [Test]
    public void PendingChanges_DrivesFilterAndDisplayDecoration()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddUntrackedFile("scratch.txt");
        var node = RefreshedNode(fixture);

        Assert.Multiple(() =>
        {
            Assert.That(node.Repository.HasPendingChanges, Is.True);
            Assert.That(node.MatchesFilter(FilterType.PendingChanges), Is.True);
            Assert.That(node.DisplayText, Does.Contain("*"),
                "A dirty working tree must decorate the row with the pending-changes marker.");
        });
    }

    [Test]
    public void StaleRepo_DrivesFilterAndDisplayDecoration()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit(DateTimeOffset.Now.AddDays(-60));
        var node = RefreshedNode(fixture);

        Assert.Multiple(() =>
        {
            Assert.That(node.MatchesFilter(FilterType.Stale), Is.True);
            Assert.That(node.DisplayText, Does.Contain("d)"),
                "A stale repo (>30 days) must show its day count in the row label.");
        });
    }

    [Test]
    public void DetachedHeadWithMatchingBranch_DrivesFilterDisplayAndCheckout()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.DetachHead();
        var node = RefreshedNode(fixture);

        Assert.Multiple(() =>
        {
            Assert.That(node.HasMatchingBranch, Is.True);
            Assert.That(node.MatchingBranchName, Is.AnyOf("main", "master"));
            Assert.That(node.MatchesFilter(FilterType.DetachedHead), Is.True);
            Assert.That(node.DisplayText, Does.Contain(node.MatchingBranchName!),
                "A detached HEAD with a known branch must show that branch in the row label.");
        });

        // CheckoutMatchingBranch re-checks out the matched branch tip; it must run without throwing.
        Assert.DoesNotThrow(node.CheckoutMatchingBranch);
    }

    [Test]
    public void CheckoutMatchingBranch_NoOpWhenNoMatchingBranch()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var node = RefreshedNode(fixture); // on a branch, so MatchingBranchName is null

        Assert.That(node.MatchingBranchName, Is.Null);
        Assert.DoesNotThrow(node.CheckoutMatchingBranch); // returns early, no checkout
    }

    [Test]
    public void MergedBranch_DrivesDownstreamFilterAndDisplayDecoration()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("feature", "feature.txt");
        fixture.MergeBranchNoFastForward("feature");
        var node = RefreshedNode(fixture);

        Assert.Multiple(() =>
        {
            Assert.That(node.MatchesFilter(FilterType.DownstreamBranches), Is.True);
            Assert.That(node.DisplayText, Does.Contain("branch"),
                "A repo with a merged downstream branch must show the merged-branch count.");
        });
    }

    [Test]
    public void BehindRemote_DrivesFilterDisplayDecorationAndTooltip()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.AdvanceOriginIndependently("upstream-change.txt");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(fetchRemotes: true);
        var node = new RepositoryNodeViewModel(repo);

        Assert.Multiple(() =>
        {
            Assert.That(node.MatchesFilter(FilterType.BehindRemote), Is.True);
            Assert.That(node.DisplayText, Does.Contain("↓(1)"),
                "A repo behind its remote must show the behind-remote count in the row label.");
            Assert.That(node.BehindRemoteTooltip, Does.Contain("1 commit behind"));
            Assert.That(node.BehindRemoteTooltip, Does.Contain("last fetch"),
                "The tooltip must cite the fetch timestamp so a stale comparison is visible.");
        });
    }

    [Test]
    public void NotBehindRemote_HasNoBehindRemoteTooltip()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var node = RefreshedNode(fixture);

        Assert.Multiple(() =>
        {
            Assert.That(node.MatchesFilter(FilterType.BehindRemote), Is.False);
            Assert.That(node.BehindRemoteTooltip, Is.Null);
        });
    }

    [Test]
    public void MyRepositories_MatchesAuthoredEmail()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var node = RefreshedNode(fixture);

        Assert.Multiple(() =>
        {
            Assert.That(node.MatchesFilter(FilterType.MyRepositories, "test@example.com"), Is.True,
                "The repo's author email must satisfy the My-Repositories filter.");
            Assert.That(node.MatchesFilter(FilterType.MyRepositories, "someone-else@example.com"), Is.False);
        });
    }

    [Test]
    public void UninitializedSubmodule_DrivesSubmoduleFilters()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddUninitializedSubmodule("sub");
        var node = RefreshedNode(fixture);

        Assert.Multiple(() =>
        {
            Assert.That(node.MatchesFilter(FilterType.SubmoduleUninitialized), Is.True);
            Assert.That(node.MatchesFilter(FilterType.SubmoduleConfigIssue), Is.True);
            // A repo that HAS submodules but no expanded child nodes still evaluates the checkout
            // predicate (and finds nothing actionable without children).
            Assert.That(node.MatchesFilter(FilterType.SubmoduleCheckout), Is.False);
        });
    }

    [Test]
    public void SubmoduleIssueTooltip_ListsIssuesWhenPresent()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddUninitializedSubmodule("sub");
        var node = RefreshedNode(fixture);
        node.Status = RefreshStatus.SubmoduleIssue;

        Assert.That(node.Repository.HasSubmoduleIssues, Is.True);
        Assert.That(node.StatusTooltip, Does.StartWith("Submodule issues:"),
            "With real submodule health entries the tooltip must enumerate them.");
    }

    [Test]
    public void SubmoduleIssueTooltip_FallsBackWhenNoHealthEntries()
    {
        var node = new RepositoryNodeViewModel(new GitWizardRepository("/x"))
        {
            Status = RefreshStatus.SubmoduleIssue,
        };

        Assert.That(node.StatusTooltip, Is.EqualTo("Submodule issue"),
            "With no submodule-health entries the tooltip uses the generic fallback.");
    }

    [Test]
    public void Update_DerivesTimeoutAndErrorStatusFromRefreshError()
    {
        var timedOut = new GitWizardRepository("/x") { RefreshError = "Timed out after 5s" };
        var errored = new GitWizardRepository("/y") { RefreshError = "fatal: boom" };

        var timeoutNode = new RepositoryNodeViewModel(timedOut);
        var errorNode = new RepositoryNodeViewModel(errored);
        timeoutNode.Update();
        errorNode.Update();

        Assert.Multiple(() =>
        {
            Assert.That(timeoutNode.Status, Is.EqualTo(RefreshStatus.Timeout));
            Assert.That(errorNode.Status, Is.EqualTo(RefreshStatus.Error));
        });
    }
}
