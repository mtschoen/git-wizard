using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

/// <summary>
/// Repositories arriving one at a time while a scan streams in, under remote-URL grouping where a
/// group only appears once a second repository shares the remote.
/// </summary>
[TestFixture]
public class RepositoryListGroupArrivalTests
{
    const string SharedRemote = "https://github.com/user/shared";

    static GitWizardRepository Repository(string workingDirectory, string remoteUrl)
    {
        var repository = new GitWizardRepository(workingDirectory);
        repository.RemoteUrls.Add(remoteUrl);
        return repository;
    }

    [Test]
    public void StreamingArrivals_UnderRemoteGrouping_HoldTheGroupBackUntilASecondMemberArrives()
    {
        var list = TestViewModels.Create().RepositoryList;
        list.ToggleGroupMode(GroupMode.RemoteUrl);

        list.AddRepository(Repository("C:/repos/a", SharedRemote), streaming: true);
        Assert.That(list.Repositories, Is.Empty, "A single-member remote group stays pending.");

        list.AddRepository(Repository("C:/repos/b", SharedRemote), streaming: true);

        var header = list.Repositories.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.IsGroupHeader, Is.True);
            Assert.That(header.Children.Select(child => child.WorkingDirectory), Is.EquivalentTo(["C:/repos/a", "C:/repos/b"]));
        }
    }

    [Test]
    public void StreamingArrival_IntoAnExpandedGroup_ShowsTheNewRowUnderItsHeader()
    {
        var list = TestViewModels.Create().RepositoryList;
        list.ToggleGroupMode(GroupMode.RemoteUrl);
        list.AddRepository(Repository("C:/repos/a", SharedRemote), streaming: true);
        list.AddRepository(Repository("C:/repos/b", SharedRemote), streaming: true);
        var header = list.Repositories.Single();
        list.ToggleGroupExpand(header);

        list.AddRepository(Repository("C:/repos/c", SharedRemote), streaming: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.Children, Has.Count.EqualTo(3));
            Assert.That(list.Repositories.Skip(1).Select(row => row.WorkingDirectory),
                Is.EquivalentTo(["C:/repos/a", "C:/repos/b", "C:/repos/c"]));
        }
    }

    [Test]
    public void AddRepository_SamePathTwiceWithoutGrouping_KeepsOneRow()
    {
        var list = TestViewModels.Create().RepositoryList;

        list.AddRepository(new GitWizardRepository("C:/repos/a"), streaming: true);
        list.AddRepository(new GitWizardRepository("C:/repos/a"), streaming: true);

        Assert.That(list.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void AddRepositoriesBatch_EmptyBatch_ChangesNothing()
    {
        var list = TestViewModels.Create().RepositoryList;

        list.AddRepositoriesBatch([], streaming: false);

        Assert.That(list.Repositories, Is.Empty);
    }

    [Test]
    public void AddRepositoriesBatch_PathAlreadyShown_ReplacesTheRowAndAPathRepeatedInTheBatchKeepsTheLastArrival()
    {
        var list = TestViewModels.Create().RepositoryList;
        list.AddRepository(new GitWizardRepository("C:/repos/a"), streaming: false);
        var freshA = new GitWizardRepository("C:/repos/a");
        var firstB = new GitWizardRepository("C:/repos/b");
        var lastB = new GitWizardRepository("C:/repos/b");

        list.AddRepositoriesBatch([freshA, firstB, lastB], streaming: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(list.Repositories.Select(node => node.WorkingDirectory), Is.EqualTo(["C:/repos/a", "C:/repos/b"]));
            Assert.That(list.Repositories[0].Repository, Is.SameAs(freshA));
            Assert.That(list.Repositories[1].Repository, Is.SameAs(lastB));
        }
    }

    [TestCase("C:/repos/unknown-parent", "C:/repos/unknown-parent/vendor/lib", TestName = "AddSubmodule_ParentNotShown_IsIgnored")]
    [TestCase("C:/repos/parent", "", TestName = "AddSubmodule_SubmoduleWithoutAPath_IsIgnored")]
    public void AddSubmodule_UnusableArguments_AddNothing(string parentPath, string submodulePath)
    {
        var list = TestViewModels.Create().RepositoryList;
        var parent = new GitWizardRepository("C:/repos/parent");
        list.AddRepository(parent, streaming: false);

        list.AddSubmodule(new GitWizardRepository(parentPath), new GitWizardRepository(submodulePath));

        Assert.That(list.Repositories.Single().Children, Is.Empty);
    }
}
