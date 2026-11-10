using GitWizard.Watch;

namespace GitWizardTests.Watch;

/// <summary>
/// <see cref="WatchPath"/> compares Windows and POSIX path shapes on whichever platform the test
/// runs, so these cases pin the shape rules rather than the host's file system.
/// </summary>
[TestFixture]
public class WatchPathTests
{
    [TestCase(@"C:\work\.git", @"C:\work", TestName = "GitDirectoryParent_WindowsDrivePath")]
    [TestCase(@"C:\.git", @"C:\", TestName = "GitDirectoryParent_DriveRootKeepsItsSeparator")]
    [TestCase("/.git", "/", TestName = "GitDirectoryParent_PosixRootKeepsItsSeparator")]
    [TestCase("/srv/repos/.git", "/srv/repos", TestName = "GitDirectoryParent_PosixPath")]
    public void TryGetGitDirectoryParent_NamesTheDirectoryContainingTheGitDirectory(string gitDirectory, string expectedParent)
    {
        var found = WatchPath.Parse(gitDirectory).TryGetGitDirectoryParent(out var parent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(parent.Original, Is.EqualTo(expectedParent));
        }
    }

    [TestCase("/srv/repos/work", TestName = "NotAGitDirectory_DifferentName")]
    [TestCase("work", TestName = "NotAGitDirectory_NoSeparator")]
    public void TryGetGitDirectoryParent_OtherPaths_AreRejected(string path)
    {
        Assert.That(WatchPath.Parse(path).TryGetGitDirectoryParent(out _), Is.False);
    }

    [Test]
    public void IsEqualToChildOf_PathsOfDifferentShape_NeverMatch()
    {
        Assert.That(WatchPath.Parse("/work/repo").IsEqualToChildOf(WatchPath.Parse(@"C:\work"), "repo"), Is.False);
    }

    [TestCase(@"\\server", @"\\server", TestName = "Volume_UncWithoutShare_IsTheWholePath")]
    [TestCase(@"\\server\share\dir", @"\\server\share", TestName = "Volume_UncShare")]
    [TestCase(@"\rooted", @"\", TestName = "Volume_RootedWithoutDrive")]
    [TestCase("relative", null, TestName = "Volume_RelativePosixPath_HasNone")]
    public void Parse_Volume_FollowsPathShape(string path, string? expectedVolume)
    {
        Assert.That(WatchPath.Parse(path).Volume, Is.EqualTo(expectedVolume));
    }

    [Test]
    public void StringComparer_TreatsMissingPathsAsEqualOnlyToEachOther()
    {
        var comparer = WatchPath.StringComparer;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(comparer.Equals(null, null), Is.True);
            Assert.That(comparer.Equals(null, "/a"), Is.False);
            Assert.That(comparer.Equals("/a", null), Is.False);
            Assert.That(comparer.Equals(@"C:\Work\", "c:/work"), Is.True);
        }
    }
}
