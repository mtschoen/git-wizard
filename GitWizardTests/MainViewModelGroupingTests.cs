using GitWizard;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

/// <summary>
/// Exercises the grouping/sorting/filtering logic in <c>MainViewModel.Grouping</c> by seeding
/// repositories deterministically through the internal <c>AddRepository</c> seam (rather than the
/// async UI command queue), then driving the public Apply*/SetSearchText entry points and inspecting
/// the resulting <see cref="MainViewModel.Repositories"/> collection.
/// </summary>
public class MainViewModelGroupingTests
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

    [Test]
    public void GroupByRemoteUrl_GroupsDuplicateRemotesAcrossUrlForms()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a/clone1", "git@github.com:user/widget.git"));
        viewModel.AddRepository(Repo("/b/clone2", "https://github.com/user/widget"));
        viewModel.AddRepository(Repo("/c/solo", "https://github.com/user/other"));

        viewModel.ApplyGroup("GroupByRemoteUrl");

        // Only the duplicated remote (2 copies, given in SSH + HTTPS form that normalize equal) forms
        // a visible group; the singleton is dropped.
        var headers = viewModel.Repositories.Where(n => n.IsGroupHeader).ToList();
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
        viewModel.AddRepository(Repo("/a/r1", "https://github.com/u/one"));
        viewModel.AddRepository(Repo("/b/r2", "https://github.com/u/two"));

        viewModel.ApplyGroup("GroupByRemoteUrl");

        Assert.That(viewModel.Repositories.Where(n => n.IsGroupHeader), Is.Empty,
            "Remote-URL grouping only surfaces groups with 2+ copies (the duplicates worth cleaning up).");
    }

    [Test]
    public void SortByRemoteUrl_OrdersByNormalizedRemoteWithNoRemoteLast()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/z", "https://github.com/u/zebra"));
        viewModel.AddRepository(Repo("/a", "https://github.com/u/apple"));
        viewModel.AddRepository(Repo("/n")); // no remote -> sorts to the end

        viewModel.ApplySort("SortByRemoteUrl");

        var directories = viewModel.Repositories.Select(n => n.WorkingDirectory).ToList();
        Assert.That(directories, Is.EqualTo(new[] { "/a", "/z", "/n" }));
    }

    [Test]
    public void SearchText_FiltersByWorkingDirectorySubstring()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/projects/alpha-service"));
        viewModel.AddRepository(Repo("/projects/beta-tool"));

        viewModel.SetSearchText("ALPHA"); // case-insensitive

        var directories = viewModel.Repositories.Select(n => n.WorkingDirectory).ToList();
        Assert.That(directories, Is.EqualTo(new[] { "/projects/alpha-service" }));
    }

    [Test]
    public void ToggleGroupExpand_InsertsAndRemovesChildren()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a/clone1", "https://github.com/u/x"));
        viewModel.AddRepository(Repo("/b/clone2", "https://github.com/u/x"));
        viewModel.ApplyGroup("GroupByRemoteUrl");

        var header = viewModel.Repositories.Single(n => n.IsGroupHeader);
        var collapsedCount = viewModel.Repositories.Count;
        Assert.That(header.IsExpanded, Is.False);

        viewModel.ToggleGroupExpand(header);
        Assert.Multiple(() =>
        {
            Assert.That(header.IsExpanded, Is.True);
            Assert.That(viewModel.Repositories.Count, Is.EqualTo(collapsedCount + 2),
                "Expanding inserts the two children after the header.");
        });

        viewModel.ToggleGroupExpand(header);
        Assert.Multiple(() =>
        {
            Assert.That(header.IsExpanded, Is.False);
            Assert.That(viewModel.Repositories.Count, Is.EqualTo(collapsedCount),
                "Collapsing removes the children again.");
        });
    }

    [Test]
    public void UpdateCompletedRepository_WhenGrouping_RefreshesParentHeader()
    {
        var viewModel = NewViewModel();
        var first = Repo("/a/clone1", "https://github.com/u/x");
        viewModel.AddRepository(first);
        viewModel.AddRepository(Repo("/b/clone2", "https://github.com/u/x"));
        viewModel.ApplyGroup("GroupByRemoteUrl");
        var header = viewModel.Repositories.Single(n => n.IsGroupHeader);

        // The completed repo lives under a group header; the update must find and refresh that
        // header without throwing.
        Assert.DoesNotThrow(() => viewModel.UpdateCompletedRepository(first));
        Assert.That(viewModel.Repositories, Does.Contain(header));
    }

    [Test]
    public void OpenInExplorer_GroupHeader_TogglesExpansion()
    {
        var viewModel = NewViewModel();
        viewModel.AddRepository(Repo("/a/clone1", "https://github.com/u/x"));
        viewModel.AddRepository(Repo("/b/clone2", "https://github.com/u/x"));
        viewModel.ApplyGroup("GroupByRemoteUrl");
        var header = viewModel.Repositories.Single(n => n.IsGroupHeader);

        // For a group header, OpenInExplorer routes to expand/collapse rather than launching a folder.
        viewModel.OpenInExplorerCommand.Execute(header);

        Assert.That(header.IsExpanded, Is.True);
    }
}
