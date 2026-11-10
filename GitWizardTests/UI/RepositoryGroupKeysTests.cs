using GitWizard;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

/// <summary>
/// The group keys a repository row files under and the rename correlation the refresh uses to
/// recognise a moved clone, for the shapes the grouping tests in <c>MainViewModelGroupingTests</c> do not reach.
/// </summary>
[TestFixture]
public class RepositoryGroupKeysTests
{
    static RepositoryNodeViewModel Node(string workingDirectory, params string[] remoteUrls)
    {
        var repository = new GitWizardRepository(workingDirectory);
        repository.RemoteUrls.AddRange(remoteUrls);
        return new RepositoryNodeViewModel(repository);
    }

    [Test]
    public void GetGroupKeys_DriveModeWithoutAWorkingDirectory_FilesUnderUnknown()
    {
        Assert.That(RepositoryGroupKeys.GetGroupKeys(Node(string.Empty), GroupMode.Drive), Is.EqualTo(["(unknown)"]));
    }

    [Test]
    public void GetGroupKeys_NoGrouping_FilesUnderUnknown()
    {
        Assert.That(RepositoryGroupKeys.GetGroupKeys(Node("C:/repos/a"), GroupMode.None), Is.EqualTo(["(unknown)"]));
    }

    [Test]
    public void GetGroupKeys_RemoteModeWithoutRemotes_FilesUnderNoRemote()
    {
        Assert.That(RepositoryGroupKeys.GetGroupKeys(Node("C:/repos/a"), GroupMode.RemoteUrl), Is.EqualTo(["(no remote)"]));
    }

    [Test]
    public void GetGroupKeys_RemoteModeWithSeveralRemotes_FilesUnderEachDistinctNormalizedUrl()
    {
        var keys = RepositoryGroupKeys.GetGroupKeys(
            Node("C:/repos/a", "https://github.com/user/repo.git", "git@github.com:User/Repo", "https://gitea.example/user/repo"),
            GroupMode.RemoteUrl);

        Assert.That(keys, Is.EqualTo(["github.com/user/repo", "gitea.example/user/repo"]));
    }

    [Test]
    public void NormalizeRemoteUrl_ScpStyleUrlWithoutAPath_IsOnlyLowerCased()
    {
        Assert.That(RepositoryGroupKeys.NormalizeRemoteUrl("git@Host"), Is.EqualTo("git@host"));
    }

    [Test]
    public void FindRenamedRepo_ReturnsTheOtherCloneSharingARemote()
    {
        var errored = new GitWizardRepository("C:/old/name");
        errored.RemoteUrls.Add("https://github.com/user/repo.git");
        var healthy = new Dictionary<string, GitWizardRepository>
        {
            ["C:/old/name"] = errored,
            ["C:/other/unrelated"] = Node("C:/other/unrelated", "https://github.com/user/different").Repository,
            ["C:/new/name"] = Node("C:/new/name", "git@github.com:user/repo").Repository
        };

        Assert.That(RepositoryGroupKeys.FindRenamedRepo(errored, "C:/old/name", healthy), Is.EqualTo("C:/new/name"));
    }

    [Test]
    public void FindRenamedRepo_WithoutARemoteOrAMatch_ReturnsNull()
    {
        var withoutRemote = new GitWizardRepository("C:/old/name");
        var withRemote = new GitWizardRepository("C:/old/name");
        withRemote.RemoteUrls.Add("https://github.com/user/repo");
        var healthy = new Dictionary<string, GitWizardRepository>
        {
            ["C:/other"] = Node("C:/other", "https://github.com/user/different").Repository
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RepositoryGroupKeys.FindRenamedRepo(withoutRemote, "C:/old/name", healthy), Is.Null);
            Assert.That(RepositoryGroupKeys.FindRenamedRepo(withRemote, "C:/old/name", healthy), Is.Null);
        }
    }
}
