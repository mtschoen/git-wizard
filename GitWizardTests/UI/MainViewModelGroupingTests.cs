using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

/// <summary>
/// Exercises the grouping/sorting/filtering logic in <c>RepositoryListViewModel</c> by seeding
/// repositories deterministically through the internal <c>AddRepository</c> seam (rather than the
/// async UI command queue), then driving the public Apply*/SetSearchText entry points and inspecting
/// the resulting <see cref="RepositoryListViewModel.Repositories"/> collection.
/// </summary>
// Grouping tests exercise shared group-publication and hierarchical view-model scenarios across display filters.
// Covers RepositoryListViewModel.cs, RepositoryListViewModel.Groups.cs, RepositoryListViewModel.Rows.cs, RepositoryGroupKeys.cs, RepositorySortOrder.cs, and StatusHeaderViewModel.cs.
public class MainViewModelGroupingTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel NewViewModel() => TestViewModels.Create();

    static GitWizardRepository Repo(string workingDirectory, params string[] remotes)
    {
        var repository = new GitWizardRepository(workingDirectory);
        foreach (var url in remotes)
            repository.RemoteUrls.Add(url);
        return repository;
    }

    [TestCase("https://github.com/user/widget", "https://github.com/user/widget.git")]
    [TestCase("git@github.com:user/widget.git", "https://github.com/user/widget")]
    [TestCase("http://github.com/user/widget", "http://github.com/user/widget")]
    [TestCase("https://GitHub.COM/User/Widget.git", "https://github.com/user/widget")]
    public void GroupByRemoteUrl_GroupsDuplicateRemotesAcrossUrlForms(string remote1, string remote2)
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/a/clone1", remote1), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/b/clone2", remote2), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/c/solo", "https://github.com/user/other"), streaming: false);

        viewModel.RepositoryList.ToggleGroupMode(GroupMode.RemoteUrl);

        var headers = viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader).ToList();
        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(headers[0].GroupKey, Is.EqualTo("github.com/user/widget"));
            Assert.That(headers[0].Children, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void GroupByRemoteUrl_DropsSingletonGroups()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/a/r1", "https://github.com/u/one"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/b/r2", "https://github.com/u/two"), streaming: false);

        viewModel.RepositoryList.ToggleGroupMode(GroupMode.RemoteUrl);

        Assert.That(viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader), Is.Empty,
            "Remote-URL grouping only surfaces groups with 2+ copies (the duplicates worth cleaning up).");
    }

    [Test]
    public void GroupByRemoteUrl_NoRemote_GroupsAsSeparateLabel()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/a"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/b"), streaming: false);

        viewModel.RepositoryList.ToggleGroupMode(GroupMode.RemoteUrl);

        var headers = viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader).ToList();
        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.That(headers[0].GroupKey, Is.EqualTo("(no remote)"));
    }

    [Test]
    public void GroupByDrive_GroupsRepositoriesByDriveRoot()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("C:\\repos\\alpha"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("C:\\repos\\beta"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("D:\\projects\\gamma"), streaming: false);

        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        var headers = viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader).ToList();
        var expectedCount = OperatingSystem.IsWindows() ? 2 : 1;
        Assert.That(headers, Has.Count.EqualTo(expectedCount));

        if (OperatingSystem.IsWindows())
        {
            var groupKeys = headers.Select(h => h.GroupKey).OrderBy(k => k).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(groupKeys[0], Is.EqualTo("C:\\"));
                Assert.That(groupKeys[1], Is.EqualTo("D:\\"));
            });
        }
    }

    [Test]
    public void GroupByDrive_SingleDrive_ShowsSingleGroup()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("C:\\repos\\one"), streaming: false);

        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        var headers = viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader).ToList();
        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.That(headers[0].Children, Has.Count.EqualTo(1));
    }

    [Test]
    public void GroupByDrive_SortedByGroupSizeDescending()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("D:\\r1"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("C:\\r1"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("C:\\r2"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("C:\\r3"), streaming: false);

        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        var headers = viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader).ToList();
        var expectedMinCount = OperatingSystem.IsWindows() ? 2 : 1;
        Assert.That(headers, Has.Count.GreaterThanOrEqualTo(expectedMinCount));

        if (OperatingSystem.IsWindows())
        {
            Assert.That(headers[0].Children.Count, Is.GreaterThanOrEqualTo(headers[1].Children.Count));
        }
    }

    [Test]
    public void GroupHeader_DisplayText_IncludesCountAndGroupKey()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/a/clone1", "https://github.com/u/x"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/b/clone2", "https://github.com/u/x"), streaming: false);

        viewModel.RepositoryList.ToggleGroupMode(GroupMode.RemoteUrl);

        var header = viewModel.RepositoryList.Repositories.Single(n => n.IsGroupHeader);
        Assert.Multiple(() =>
        {
            Assert.That(header.DisplayText, Does.Contain("(2)"));
            Assert.That(header.DisplayText, Does.Contain("github.com/u/x"));
        });
    }

    [Test]
    public void UpdateHeaderWithFilterInfo_NoFilterNoGroup_ShowsRepoCount()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/a"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/b"), streaming: false);

        viewModel.RepositoryList.SetSearchText("");

        Assert.That(viewModel.StatusHeader.HeaderText, Does.Contain("2"));
    }

    [Test]
    public void UpdateHeaderWithFilterInfo_WithGroup_ShowsGroupCountInHeader()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("C:\\r1"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("D:\\r2"), streaming: false);

        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        Assert.That(viewModel.StatusHeader.HeaderText, Does.Contain("groups"));
    }

    [Test]
    public void UpdateHeaderWithFilterInfo_WithFilter_ShowsFilteredCount()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/a"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/b"), streaming: false);

        viewModel.RepositoryList.ToggleFilter(FilterType.PendingChanges);

        Assert.That(viewModel.StatusHeader.HeaderText, Does.Contain("Showing"));
    }

    [Test]
    public void SortByRemoteUrl_OrdersByNormalizedRemoteWithNoRemoteLast()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/z", "https://github.com/u/zebra"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/a", "https://github.com/u/apple"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/n"), streaming: false); // no remote -> sorts to the end

        viewModel.RepositoryList.SetSortMode(SortMode.RemoteUrl);

        var directories = viewModel.RepositoryList.Repositories.Select(n => n.WorkingDirectory).ToList();
        Assert.That(directories, Is.EqualTo(new[] { "/a", "/z", "/n" }));
    }

    [Test]
    public void SortBySizeOnDisk_OrdersByDescendingSize()
    {
        var viewModel = NewViewModel();
        var small = Repo("/small");
        var large = Repo("/large");
        small.SizeOnDisk = 100;
        large.SizeOnDisk = 10000;
        viewModel.RepositoryList.AddRepository(small, streaming: false);
        viewModel.RepositoryList.AddRepository(large, streaming: false);

        viewModel.RepositoryList.SetSortMode(SortMode.SizeOnDisk);

        var dirs = viewModel.RepositoryList.Repositories.Select(n => n.WorkingDirectory).ToList();
        Assert.That(dirs[0], Is.EqualTo("/large"));
    }

    [Test]
    public void SortByRecentlyUsed_OrdersByDescendingLastCommitDate()
    {
        var viewModel = NewViewModel();
        var old = Repo("/old");
        var recent = Repo("/recent");
        old.LastCommitDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        recent.LastCommitDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        viewModel.RepositoryList.AddRepository(old, streaming: false);
        viewModel.RepositoryList.AddRepository(recent, streaming: false);

        viewModel.RepositoryList.SetSortMode(SortMode.RecentlyUsed);

        var dirs = viewModel.RepositoryList.Repositories.Select(n => n.WorkingDirectory).ToList();
        Assert.That(dirs[0], Is.EqualTo("/recent"));
    }

    [Test]
    public void SortByWorkingDirectory_DefaultSort()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/z"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/a"), streaming: false);

        viewModel.RepositoryList.SetSortMode(SortMode.WorkingDirectory);

        var dirs = viewModel.RepositoryList.Repositories.Select(n => n.WorkingDirectory).ToList();
        Assert.That(dirs, Is.EqualTo(new[] { "/a", "/z" }));
    }

    [Test]
    public void SwitchGroupMode_FromDriveToRemote_RebuildsGroups()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("C:\\a", "https://github.com/u/x"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("D:\\b", "https://github.com/u/x"), streaming: false);

        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);
        var driveHeaders = viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader).ToList();

        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);
        viewModel.RepositoryList.ToggleGroupMode(GroupMode.RemoteUrl);
        var remoteHeaders = viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader).ToList();

        var expectedDriveCount = OperatingSystem.IsWindows() ? 2 : 1;
        Assert.Multiple(() =>
        {
            Assert.That(driveHeaders, Has.Count.EqualTo(expectedDriveCount));
            Assert.That(remoteHeaders, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void SearchText_FiltersByWorkingDirectorySubstring()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/projects/alpha-service"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/projects/beta-tool"), streaming: false);

        viewModel.RepositoryList.SetSearchText("ALPHA");

        var directories = viewModel.RepositoryList.Repositories.Select(n => n.WorkingDirectory).ToList();
        Assert.That(directories, Is.EqualTo(new[] { "/projects/alpha-service" }));
    }

    [Test]
    public void SearchText_EmptyString_ShowsAll()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/alpha"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/beta"), streaming: false);

        viewModel.RepositoryList.SetSearchText("");

        Assert.That(viewModel.RepositoryList.Repositories, Has.Count.EqualTo(2));
    }

    [Test]
    public void SearchText_NoMatch_ShowsEmpty()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/alpha"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/beta"), streaming: false);

        viewModel.RepositoryList.SetSearchText("NONEXISTENT");

        Assert.That(viewModel.RepositoryList.Repositories, Has.Count.EqualTo(0));
    }

    [Test]
    public void SearchText_PartialMatch_FiltersCorrectly()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/projects/alpha-service"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/projects/beta-tool"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/projects/alpha-ui"), streaming: false);

        viewModel.RepositoryList.SetSearchText("alpha");

        Assert.That(viewModel.RepositoryList.Repositories, Has.Count.EqualTo(2));
    }

    [Test]
    public void SearchText_CombinedWithGrouping_FiltersGroupedRepos()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("C:\\alpha\\r1"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("C:\\beta\\r2"), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("C:\\alpha\\r3"), streaming: false);

        viewModel.RepositoryList.SetSearchText("alpha");
        viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        var headers = viewModel.RepositoryList.Repositories.Where(n => n.IsGroupHeader).ToList();
        if (headers.Count > 0)
        {
            var totalChildren = headers.Sum(h => h.Children.Count);
            Assert.That(totalChildren, Is.EqualTo(2));
        }
        else
        {
            Assert.That(viewModel.RepositoryList.Repositories, Has.Count.EqualTo(2));
        }
    }

    [TestCase(GroupMode.RemoteUrl)]
    [TestCase(GroupMode.Drive)]
    public void ToggleGroupExpand_InsertsAndRemovesChildren(GroupMode mode)
    {
        var viewModel = NewViewModel();
        if (mode == GroupMode.RemoteUrl)
        {
            viewModel.RepositoryList.AddRepository(Repo("/a/clone1", "https://github.com/u/x"), streaming: false);
            viewModel.RepositoryList.AddRepository(Repo("/b/clone2", "https://github.com/u/x"), streaming: false);
        }
        else
        {
            viewModel.RepositoryList.AddRepository(Repo("C:\\repos\\alpha"), streaming: false);
            viewModel.RepositoryList.AddRepository(Repo("C:\\repos\\beta"), streaming: false);
        }
        viewModel.RepositoryList.ToggleGroupMode(mode);

        var header = viewModel.RepositoryList.Repositories.Single(n => n.IsGroupHeader);
        var collapsedCount = viewModel.RepositoryList.Repositories.Count;
        Assert.That(header.IsExpanded, Is.False);

        viewModel.RepositoryList.ToggleGroupExpand(header);
        Assert.Multiple(() =>
        {
            Assert.That(header.IsExpanded, Is.True);
            Assert.That(viewModel.RepositoryList.Repositories.Count, Is.EqualTo(collapsedCount + 2),
                "Expanding inserts the two children after the header.");
            Assert.That(viewModel.RepositoryList.Repositories[1].IsGroupHeader, Is.False);
            Assert.That(viewModel.RepositoryList.Repositories[2].IsGroupHeader, Is.False);
        });

        viewModel.RepositoryList.ToggleGroupExpand(header);
        Assert.Multiple(() =>
        {
            Assert.That(header.IsExpanded, Is.False);
            Assert.That(viewModel.RepositoryList.Repositories.Count, Is.EqualTo(collapsedCount),
                "Collapsing removes the children again.");
        });
    }

    [Test]
    public void ToggleGroupExpand_NullNode_IsNoOp()
    {
        var vm = NewViewModel();
        vm.RepositoryList.ToggleGroupExpand(null);
        Assert.That(vm.RepositoryList.Repositories, Is.Empty);
    }

    [Test]
    public void ToggleGroupExpand_NonGroupHeaderNode_IsNoOp()
    {
        var vm = NewViewModel();
        var node = new RepositoryNodeViewModel(new GitWizardRepository("C:/repos/alpha"));
        vm.RepositoryList.Repositories.Add(node);

        vm.RepositoryList.ToggleGroupExpand(node);

        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void ToggleGroupExpand_HeaderNotInRepositories_IsNoOp()
    {
        var vm = NewViewModel();
        var header = RepositoryNodeViewModel.CreateGroupHeader("C:\\");

        vm.RepositoryList.ToggleGroupExpand(header);

        Assert.That(vm.RepositoryList.Repositories, Is.Empty);
    }

    [Test]
    public void ToggleGroupExpand_InvokesScrollToRequest()
    {
        var vm = NewViewModel();
        vm.RepositoryList.ToggleGroupMode(GroupMode.Drive);
        vm.RepositoryList.AddRepository(new GitWizardRepository("C:/repos/alpha"), streaming: false);

        var header = vm.RepositoryList.Repositories[0];
        RepositoryNodeViewModel? scrolledTo = null;
        vm.RepositoryList.ScrollToRequest = n => scrolledTo = n;

        vm.RepositoryList.ToggleGroupExpand(header);

        Assert.That(scrolledTo, Is.SameAs(header));
    }

    [Test]
    public void UpdateCompletedRepository_WhenGrouping_RefreshesParentHeader()
    {
        var viewModel = NewViewModel();
        var first = Repo("/a/clone1", "https://github.com/u/x");
        viewModel.RepositoryList.AddRepository(first, streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/b/clone2", "https://github.com/u/x"), streaming: false);
        viewModel.RepositoryList.ToggleGroupMode(GroupMode.RemoteUrl);
        var header = viewModel.RepositoryList.Repositories.Single(n => n.IsGroupHeader);

        Assert.DoesNotThrow(() => viewModel.RepositoryList.UpdateCompletedRepository(first, streaming: false));
        Assert.That(viewModel.RepositoryList.Repositories, Does.Contain(header));
    }

    [TestCase(GroupMode.RemoteUrl)]
    [TestCase(GroupMode.Drive)]
    public void OpenInExplorer_GroupHeader_TogglesExpansion(GroupMode mode)
    {
        var viewModel = NewViewModel();
        if (mode == GroupMode.RemoteUrl)
        {
            viewModel.RepositoryList.AddRepository(Repo("/a/clone1", "https://github.com/u/x"), streaming: false);
            viewModel.RepositoryList.AddRepository(Repo("/b/clone2", "https://github.com/u/x"), streaming: false);
        }
        else
        {
            viewModel.RepositoryList.AddRepository(Repo("C:\\repos\\alpha"), streaming: false);
            viewModel.RepositoryList.AddRepository(Repo("C:\\repos\\beta"), streaming: false);
        }
        viewModel.RepositoryList.ToggleGroupMode(mode);
        var header = viewModel.RepositoryList.Repositories.Single(n => n.IsGroupHeader);

        viewModel.OpenInExplorer(header);

        Assert.That(header.IsExpanded, Is.True);
    }

    [Test]
    public void ApplyFilterAndGrouping_EmptyRepositoryList_NoCrash()
    {
        var viewModel = NewViewModel();
        Assert.DoesNotThrow(() => viewModel.RepositoryList.ToggleGroupMode(GroupMode.Drive));
        Assert.That(viewModel.RepositoryList.Repositories, Is.Empty);
    }

    [Test]
    public void ToggleFilter_AllFilterTypes_DoNotThrow()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/a"), streaming: false);

        foreach (var filter in Enum.GetValues<FilterType>())
        {
            Assert.DoesNotThrow(() =>
            {
                viewModel.RepositoryList.ToggleFilter(filter);
                viewModel.RepositoryList.ToggleFilter(filter);
            }, $"Filter '{filter}' threw an exception.");
        }
    }

    [Test]
    public void AddToGroups_LiveUpdates_DriveGrouping()
    {
        var vm = NewViewModel();
        vm.RepositoryList.ToggleGroupMode(GroupMode.Drive);

        using var missing1 = new TempDirectory("drive1-", create: false);
        using var missing2 = new TempDirectory("drive2-", create: false);
        var repo1 = new GitWizardRepository(missing1.Path);
        var repo2 = new GitWizardRepository(missing2.Path);

        vm.RepositoryList.AddRepository(repo1, streaming: false);
        Assert.That(vm.RepositoryList.Repositories, Is.Not.Empty);

        var header = vm.RepositoryList.Repositories[0];
        Assert.That(header.IsGroupHeader, Is.True);
        Assert.That(header.Children, Has.Count.EqualTo(1));

        vm.RepositoryList.AddRepository(repo2, streaming: false);
        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1));
        Assert.That(header.Children, Has.Count.EqualTo(2));
    }

    [Test]
    public void AddToGroups_LiveUpdates_RemoteUrlGroupingAndPromotion()
    {
        var vm = NewViewModel();
        vm.RepositoryList.ToggleGroupMode(GroupMode.RemoteUrl);

        using var missing1 = new TempDirectory("repo1-", create: false);
        using var missing2 = new TempDirectory("repo2-", create: false);
        var repo1 = new GitWizardRepository(missing1.Path);
        repo1.RemoteUrls.Add("git@github.com:mtschoen/git-wizard.git");

        var repo2 = new GitWizardRepository(missing2.Path);
        repo2.RemoteUrls.Add("https://github.com/mtschoen/git-wizard.git");

        vm.RepositoryList.AddRepository(repo1, streaming: false);
        Assert.That(vm.RepositoryList.Repositories, Is.Empty);

        vm.RepositoryList.AddRepository(repo2, streaming: false);
        Assert.That(vm.RepositoryList.Repositories, Has.Count.EqualTo(1));
        Assert.That(vm.RepositoryList.Repositories[0].IsGroupHeader, Is.True);
        Assert.That(vm.RepositoryList.Repositories[0].Children, Has.Count.EqualTo(2));
    }

    [Test]
    public void NormalizeRemoteUrl_StripsDotGitSuffix()
    {
        Assert.That(RepositoryGroupKeys.NormalizeRemoteUrl("https://github.com/user/repo.git"),
            Is.EqualTo("github.com/user/repo"));
    }

    [Test]
    public void NormalizeRemoteUrl_NormalizesSshFormat()
    {
        Assert.That(RepositoryGroupKeys.NormalizeRemoteUrl("git@github.com:user/repo"),
            Is.EqualTo("github.com/user/repo"));
    }

    [Test]
    public void NormalizeRemoteUrl_StripsHttps()
    {
        Assert.That(RepositoryGroupKeys.NormalizeRemoteUrl("https://github.com/user/repo"),
            Is.EqualTo("github.com/user/repo"));
    }

    [Test]
    public void NormalizeRemoteUrl_StripsHttp()
    {
        Assert.That(RepositoryGroupKeys.NormalizeRemoteUrl("http://github.com/user/repo"),
            Is.EqualTo("github.com/user/repo"));
    }

    [Test]
    public void NormalizeRemoteUrl_LowerCases()
    {
        Assert.That(RepositoryGroupKeys.NormalizeRemoteUrl("HTTPS://GitHub.COM/User/Repo"),
            Is.EqualTo("github.com/user/repo"));
    }

    [Test]
    public void NormalizeRemoteUrl_TrimsWhitespace()
    {
        Assert.That(RepositoryGroupKeys.NormalizeRemoteUrl("  https://github.com/user/repo  "),
            Is.EqualTo("github.com/user/repo"));
    }
}
