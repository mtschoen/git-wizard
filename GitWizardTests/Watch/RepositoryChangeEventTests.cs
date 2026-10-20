using GitWizard.Watch;

namespace GitWizardTests.Watch;

[TestFixture]
public class RepositoryChangeEventTests
{
    [Test]
    public void Constructor_SetsRepoRootKindAndNewPath()
    {
        var evt = new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Renamed, @"C:\repo\b");

        Assert.That(evt.RepoRoot, Is.EqualTo(@"C:\repo\a"));
        Assert.That(evt.Kind, Is.EqualTo(RepositoryChangeKind.Renamed));
        Assert.That(evt.NewPath, Is.EqualTo(@"C:\repo\b"));
    }

    [Test]
    public void Constructor_DefaultsNewPathToNull()
    {
        var evt = new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Changed);

        Assert.That(evt.NewPath, Is.Null);
    }
}
