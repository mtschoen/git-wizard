using GitWizard;

namespace GitWizardTests;

public partial class RepositoryChangeFilterTests
{
    [Test]
    public void Classify_WindowsModifiedEntryWithDifferentCasing_MapsToChanged()
    {
        const string repoRoot = @"C:\Repos\Repo-A";
        var filter = new RepositoryChangeFilter([repoRoot]);

        var result = filter.Classify([Modified(@"c:\repos\repo-a\index.lock")]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { repoRoot }));
    }

    [Test]
    public void Classify_PosixModifiedEntryWithDifferentCasing_IsIgnored()
    {
        var filter = new RepositoryChangeFilter(["/repos/repo-a"]);

        var result = filter.Classify([Modified("/repos/Repo-A/index.lock")]);

        Assert.That(result.Changed, Is.Empty);
    }

    [Test]
    public void Classify_WindowsGitDirectoryNameWithDifferentCasing_MapsToCreated()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\Repos"]);

        var result = filter.Classify([Created(@"c:\repos\new-repo\.GIT")]);

        Assert.That(result.Created, Is.EquivalentTo(new[] { @"c:\repos\new-repo" }));
    }

    [Test]
    public void Classify_PosixGitDirectoryNameWithDifferentCasing_IsIgnored()
    {
        var filter = new RepositoryChangeFilter([], ["/repos"]);

        var result = filter.Classify([Created("/repos/new-repo/.GIT")]);

        Assert.That(result.Created, Is.Empty);
    }

    [Test]
    public void Classify_UncModifiedEntryWithDifferentCasing_EmitsOriginalRoot()
    {
        const string repositoryRoot = @"\\Server\Share\Repo";
        var filter = new RepositoryChangeFilter([repositoryRoot]);

        var result = filter.Classify([Modified(@"\\server\share\repo\file.txt")]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { repositoryRoot }));
    }

    [Test]
    public void Classify_UncGitDirectoryWithDifferentCasing_MapsToCreated()
    {
        var filter = new RepositoryChangeFilter([], [@"\\Server\Share\Repos"]);

        var result = filter.Classify([Created(@"\\server\share\repos\NewRepo\.GIT")]);

        Assert.That(result.Created, Is.EquivalentTo(new[] { @"\\server\share\repos\NewRepo" }));
    }

    [Test]
    public void Classify_RootedWindowsModifiedEntryWithDifferentCasing_EmitsOriginalRoot()
    {
        const string repositoryRoot = @"\Repos\Repo";
        var filter = new RepositoryChangeFilter([repositoryRoot]);

        var result = filter.Classify([Modified(@"\repos\repo\file.txt")]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { repositoryRoot }));
    }

    [Test]
    public void Classify_RootedWindowsGitDirectoryWithDifferentCasing_MapsToCreated()
    {
        var filter = new RepositoryChangeFilter([], [@"\Repos"]);

        var result = filter.Classify([Created(@"\repos\NewRepo\.GIT")]);

        Assert.That(result.Created, Is.EquivalentTo(new[] { @"\repos\NewRepo" }));
    }

    [Test]
    public void Classify_DriveRootModifiedEntry_EmitsAbsoluteOriginalRoot()
    {
        const string repositoryRoot = @"C:\";
        var filter = new RepositoryChangeFilter([repositoryRoot]);

        var result = filter.Classify([Modified(@"c:\file.txt")]);

        Assert.That(result.Changed, Is.EquivalentTo(new[] { repositoryRoot }));
    }

    [Test]
    public void Classify_DriveRootDeletedEntry_EmitsAbsoluteOriginalRoot()
    {
        const string repositoryRoot = @"C:\";
        var filter = new RepositoryChangeFilter([repositoryRoot]);

        var result = filter.Classify([Deleted(@"c:\")]);

        Assert.That(result.Deleted, Is.EquivalentTo(new[] { repositoryRoot }));
    }
}
