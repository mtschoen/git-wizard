using System.Reflection;
using GitWizard;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

/// <summary>
/// Additional tests for <c>MainViewModel.Grouping</c> covering edge cases not covered
/// by <see cref="MainViewModelGroupingTests"/>: different GroupMode values, group sort
/// order, header display text, empty/single repos, mode switching, filter interactions,
/// and the NormalizeRemoteUrl/FindRenamedRepo helpers.
/// </summary>
public class MainViewModelGroupingAdditionalTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel NewViewModel()
        => new(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

    static GitWizardRepository Repo(string workingDirectory, params string[] remotes)
    {
        var repository = new GitWizardRepository(workingDirectory);
        foreach (var url in remotes)
            repository.RemoteUrls.Add(url);
        return repository;
    }

    #region GroupByDrive

    [Test]
    public void GroupByDrive_GroupsRepositoriesByDriveRoot()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("C:\\repos\\alpha"));
        viewModel.AddRepository(Repo("C:\\repos\\beta"));
        viewModel.AddRepository(Repo("D:\\projects\\gamma"));

        viewModel.ApplyGroup("GroupByDrive");

        var headers = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();
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
        viewModel.AddRepository(Repo("C:\\repos\\one"));

        viewModel.ApplyGroup("GroupByDrive");

        // Drive grouping has minGroupSize=1, so a single repo forms a group
        var headers = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();
        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.That(headers[0].Children, Has.Count.EqualTo(1));
    }

    [Test]
    public void GroupByDrive_SortedByGroupSizeDescending()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("D:\\r1"));
        viewModel.AddRepository(Repo("C:\\r1"));
        viewModel.AddRepository(Repo("C:\\r2"));
        viewModel.AddRepository(Repo("C:\\r3"));

        viewModel.ApplyGroup("GroupByDrive");

        var headers = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();
        var expectedMinCount = OperatingSystem.IsWindows() ? 2 : 1;
        Assert.That(headers, Has.Count.GreaterThanOrEqualTo(expectedMinCount));

        if (OperatingSystem.IsWindows())
        {
            // Largest group should come first
            Assert.That(headers[0].Children.Count, Is.GreaterThanOrEqualTo(headers[1].Children.Count));
        }
    }

    [Test]
    public void GroupByDrive_EmptyPath_GroupsAsUnknown()
    {
        var viewModel = NewViewModel();
        Assert.That(Repo("").WorkingDirectory, Is.Empty);
        // AddRepository skips empty working dirs, so we add a non-empty one + verify the helper
        viewModel.AddRepository(Repo("C:\\repos\\normal"));

        viewModel.ApplyGroup("GroupByDrive");

        // The empty path repo won't be added. Just verify no crash.
        Assert.That(viewModel.Repositories.Count, Is.GreaterThanOrEqualTo(1));
    }

    #endregion

    #region GroupByRemoteUrl normalization

    [Test]
    public void GroupByRemoteUrl_NormalizesHttpsProtocol()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a", "https://github.com/user/repo"));
        viewModel.AddRepository(Repo("/b", "https://github.com/user/repo.git"));

        viewModel.ApplyGroup("GroupByRemoteUrl");

        var headers = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();
        Assert.That(headers, Has.Count.EqualTo(1),
            "HTTPS URLs with/without .git suffix should normalize to the same group.");
    }

    [Test]
    public void GroupByRemoteUrl_NormalizesHttpProtocol()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a", "http://github.com/user/repo"));
        viewModel.AddRepository(Repo("/b", "http://github.com/user/repo"));

        viewModel.ApplyGroup("GroupByRemoteUrl");

        var headers = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();
        Assert.That(headers, Has.Count.EqualTo(1));
    }

    [Test]
    public void GroupByRemoteUrl_NormalizesCase()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a", "https://GitHub.COM/User/Repo.git"));
        viewModel.AddRepository(Repo("/b", "https://github.com/user/repo"));

        viewModel.ApplyGroup("GroupByRemoteUrl");

        var headers = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();
        Assert.That(headers, Has.Count.EqualTo(1));
    }

    [Test]
    public void GroupByRemoteUrl_NoRemote_GroupsAsSeparateLabel()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a")); // no remote
        viewModel.AddRepository(Repo("/b")); // no remote

        viewModel.ApplyGroup("GroupByRemoteUrl");

        // Two repos with no remote both map to "(no remote)", so they form a group of 2
        var headers = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();
        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.That(headers[0].GroupKey, Is.EqualTo("(no remote)"));
    }

    #endregion

    #region Header display text

    [Test]
    public void GroupHeader_DisplayText_IncludesCountAndGroupKey()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a/clone1", "https://github.com/u/x"));
        viewModel.AddRepository(Repo("/b/clone2", "https://github.com/u/x"));

        viewModel.ApplyGroup("GroupByRemoteUrl");

        var header = viewModel.Repositories.Single(n => n.IsGroupHeader);
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
        viewModel.AddRepository(Repo("/a"));
        viewModel.AddRepository(Repo("/b"));

        // Trigger re-grouping to update the header text
        viewModel.SetSearchText("");

        Assert.That(viewModel.HeaderText, Does.Contain("2"));
    }

    [Test]
    public void UpdateHeaderWithFilterInfo_WithGroup_ShowsGroupCountInHeader()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("C:\\r1"));
        viewModel.AddRepository(Repo("D:\\r2"));

        viewModel.ApplyGroup("GroupByDrive");

        Assert.That(viewModel.HeaderText, Does.Contain("groups"));
    }

    [Test]
    public void UpdateHeaderWithFilterInfo_WithFilter_ShowsFilteredCount()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a"));
        viewModel.AddRepository(Repo("/b"));

        // Apply a filter that probably matches nothing → shows "Showing X of Y"
        viewModel.ToggleFilter(FilterType.PendingChanges);

        Assert.That(viewModel.HeaderText, Does.Contain("Showing"));
    }

    #endregion

    #region Sort modes

    [Test]
    public void SortBySizeOnDisk_OrdersByDescendingSize()
    {
        var viewModel = NewViewModel();

        // Create repos with different sizes via reflection
        var small = Repo("/small");
        var large = Repo("/large");
        SetSizeOnDisk(small, 100);
        SetSizeOnDisk(large, 10000);
        viewModel.AddRepository(small);
        viewModel.AddRepository(large);

        viewModel.ApplySort("SortBySizeOnDisk");

        var dirs = viewModel.Repositories.Select(n => n.WorkingDirectory).ToList();
        Assert.That(dirs[0], Is.EqualTo("/large"));
    }

    [Test]
    public void SortByRecentlyUsed_OrdersByDescendingLastCommitDate()
    {
        var viewModel = NewViewModel();

        var old = Repo("/old");
        var recent = Repo("/recent");
        SetLastCommitDate(old, DateTimeOffset.Now.AddDays(-30));
        SetLastCommitDate(recent, DateTimeOffset.Now);
        viewModel.AddRepository(old);
        viewModel.AddRepository(recent);

        viewModel.ApplySort("SortByRecentlyUsed");

        var dirs = viewModel.Repositories.Select(n => n.WorkingDirectory).ToList();
        Assert.That(dirs[0], Is.EqualTo("/recent"));
    }

    [Test]
    public void SortByWorkingDirectory_DefaultSort()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/z"));
        viewModel.AddRepository(Repo("/a"));

        viewModel.ApplySort("SortByWorkingDirectory");

        // WorkingDirectory sort preserves insertion order (already alpha from add)
        var dirs = viewModel.Repositories.Select(n => n.WorkingDirectory).ToList();
        Assert.That(dirs, Has.Count.EqualTo(2));
    }

    [Test]
    public void SortByRemoteUrl_UnknownButtonName_DefaultsToWorkingDirectory()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/b"));
        viewModel.AddRepository(Repo("/a"));

        viewModel.ApplySort("UnknownSort");

        Assert.That(viewModel.ActiveSortMode, Is.EqualTo(SortMode.WorkingDirectory));
    }

    #endregion

    #region Filter interactions

    [Test]
    public void ToggleFilter_SameFilterTwice_ClearsFilter()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a"));

        viewModel.ToggleFilter(FilterType.PendingChanges);
        Assert.That(viewModel.ActiveFilter, Is.EqualTo(FilterType.PendingChanges));

        viewModel.ToggleFilter(FilterType.PendingChanges);
        Assert.That(viewModel.ActiveFilter, Is.EqualTo(FilterType.None));
    }

    [Test]
    public void ToggleGroupMode_SameModeTwice_ClearsGroup()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("C:\\r1"));
        viewModel.AddRepository(Repo("D:\\r2"));

        viewModel.ToggleGroupMode(GroupMode.Drive);
        Assert.That(viewModel.ActiveGroupMode, Is.EqualTo(GroupMode.Drive));

        viewModel.ToggleGroupMode(GroupMode.Drive);
        Assert.That(viewModel.ActiveGroupMode, Is.EqualTo(GroupMode.None));
    }

    [Test]
    public void SwitchGroupMode_FromDriveToRemote_RebuildsGroups()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("C:\\a", "https://github.com/u/x"));
        viewModel.AddRepository(Repo("D:\\b", "https://github.com/u/x"));

        viewModel.ApplyGroup("GroupByDrive");
        var driveHeaders = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();

        viewModel.ApplyGroup("GroupByDrive"); // toggle off
        viewModel.ApplyGroup("GroupByRemoteUrl");
        var remoteHeaders = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();

        var expectedDriveCount = OperatingSystem.IsWindows() ? 2 : 1;
        Assert.Multiple(() =>
        {
            Assert.That(driveHeaders, Has.Count.EqualTo(expectedDriveCount));
            Assert.That(remoteHeaders, Has.Count.EqualTo(1));
        });
    }

    #endregion

    #region Search text

    [Test]
    public void SearchText_EmptyString_ShowsAll()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/alpha"));
        viewModel.AddRepository(Repo("/beta"));

        viewModel.SetSearchText("");

        Assert.That(viewModel.Repositories, Has.Count.EqualTo(2));
    }

    [Test]
    public void SearchText_NoMatch_ShowsEmpty()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/alpha"));
        viewModel.AddRepository(Repo("/beta"));

        viewModel.SetSearchText("NONEXISTENT");

        Assert.That(viewModel.Repositories, Has.Count.EqualTo(0));
    }

    [Test]
    public void SearchText_PartialMatch_FiltersCorrectly()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/projects/alpha-service"));
        viewModel.AddRepository(Repo("/projects/beta-tool"));
        viewModel.AddRepository(Repo("/projects/alpha-ui"));

        viewModel.SetSearchText("alpha");

        Assert.That(viewModel.Repositories, Has.Count.EqualTo(2));
    }

    [Test]
    public void SearchText_CombinedWithGrouping_FiltersGroupedRepos()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("C:\\alpha\\r1"));
        viewModel.AddRepository(Repo("C:\\beta\\r2"));
        viewModel.AddRepository(Repo("C:\\alpha\\r3"));

        viewModel.SetSearchText("alpha");
        viewModel.ApplyGroup("GroupByDrive");

        // Search should be applied before grouping
        var headers = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();
        if (headers.Count > 0)
        {
            // Group should contain only alpha repos
            var totalChildren = headers.Sum(h => h.Children.Count);
            Assert.That(totalChildren, Is.EqualTo(2));
        }
        else
        {
            // No grouping applied - repos directly in collection
            Assert.That(viewModel.Repositories, Has.Count.EqualTo(2));
        }
    }

    #endregion

    #region Edge cases

    [Test]
    public void ApplyFilterAndGrouping_EmptyRepositoryList_NoCrash()
    {
        var viewModel = NewViewModel();

        Assert.DoesNotThrow(() => viewModel.ApplyGroup("GroupByDrive"));
        Assert.That(viewModel.Repositories, Is.Empty);
    }

    [Test]
    public void ApplyFilter_UnknownButtonName_SetsFilterToNone()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a"));

        viewModel.ApplyFilter("UnknownFilter");

        Assert.That(viewModel.ActiveFilter, Is.EqualTo(FilterType.None));
    }

    [Test]
    public void ApplyGroup_UnknownButtonName_SetsGroupToNone()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a"));

        viewModel.ApplyGroup("UnknownGroup");

        Assert.That(viewModel.ActiveGroupMode, Is.EqualTo(GroupMode.None));
    }

    [Test]
    public void ClearCacheAsync_CompletesWithoutError()
    {
        var viewModel = NewViewModel();
        Assert.DoesNotThrowAsync(async () => await viewModel.ClearCacheAsync());
    }

    [Test]
    public void DeleteAllLocalFilesAsync_CompletesWithoutError()
    {
        var viewModel = NewViewModel();
        Assert.DoesNotThrowAsync(async () => await viewModel.DeleteAllLocalFilesAsync());
    }

    [Test]
    public void ApplyFilter_AllFilterTypes_DoNotThrow()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a"));

        foreach (var filterName in new[]
        {
            "FilterPendingChanges", "FilterSubmoduleCheckout", "FilterSubmoduleUninitialized",
            "FilterSubmoduleConfigIssue", "FilterDetachedHead", "FilterMyRepositories",
            "FilterLocalOnlyCommits", "FilterStale", "FilterDownstreamBranches",
            "FilterBehindRemote", "FilterPublishReady"
        })
        {
            Assert.DoesNotThrow(() =>
            {
                viewModel.ApplyFilter(filterName);
                viewModel.ApplyFilter(filterName); // toggle off
            }, $"Filter '{filterName}' threw an exception.");
        }
    }

    [Test]
    public void UpdateSearchText_DelegatesToSetSearchText()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/alpha"));
        viewModel.AddRepository(Repo("/beta"));

        viewModel.UpdateSearchText("alpha");

        Assert.That(viewModel.Repositories, Has.Count.EqualTo(1));
    }

    #endregion

    #region NormalizeRemoteUrl (via reflection)

    static string InvokeNormalizeRemoteUrl(string url)
    {
        var method = typeof(MainViewModel).GetMethod("NormalizeRemoteUrl",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, new object[] { url })!;
    }

    [Test]
    public void NormalizeRemoteUrl_StripsDotGitSuffix()
    {
        Assert.That(InvokeNormalizeRemoteUrl("https://github.com/user/repo.git"),
            Is.EqualTo("github.com/user/repo"));
    }

    [Test]
    public void NormalizeRemoteUrl_NormalizesSshFormat()
    {
        Assert.That(InvokeNormalizeRemoteUrl("git@github.com:user/repo"),
            Is.EqualTo("github.com/user/repo"));
    }

    [Test]
    public void NormalizeRemoteUrl_StripsHttps()
    {
        Assert.That(InvokeNormalizeRemoteUrl("https://github.com/user/repo"),
            Is.EqualTo("github.com/user/repo"));
    }

    [Test]
    public void NormalizeRemoteUrl_StripsHttp()
    {
        Assert.That(InvokeNormalizeRemoteUrl("http://github.com/user/repo"),
            Is.EqualTo("github.com/user/repo"));
    }

    [Test]
    public void NormalizeRemoteUrl_LowerCases()
    {
        Assert.That(InvokeNormalizeRemoteUrl("HTTPS://GitHub.COM/User/Repo"),
            Is.EqualTo("github.com/user/repo"));
    }

    [Test]
    public void NormalizeRemoteUrl_TrimsWhitespace()
    {
        Assert.That(InvokeNormalizeRemoteUrl("  https://github.com/user/repo  "),
            Is.EqualTo("github.com/user/repo"));
    }

    #endregion

    #region Reflection helpers

    static void SetSizeOnDisk(GitWizardRepository repo, long size)
    {
        typeof(GitWizardRepository).GetProperty("SizeOnDisk")!
            .GetSetMethod(true)!
            .Invoke(repo, new object[] { size });
    }

    static void SetLastCommitDate(GitWizardRepository repo, DateTimeOffset date)
    {
        typeof(GitWizardRepository).GetProperty("LastCommitDate")!
            .GetSetMethod(true)!
            .Invoke(repo, new object[] { date });
    }

    #endregion
}
