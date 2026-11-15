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

    [TestCase(@"C:\repos\bare\HEAD", @"C:\repos\bare", TestName = "BareCandidate_HeadLeaf")]
    [TestCase(@"C:\repos\bare\OBJECTS", @"C:\repos\bare", TestName = "BareCandidate_ObjectsLeaf_FoldsCase")]
    [TestCase(@"C:\repos\bare\refs", @"C:\repos\bare", TestName = "BareCandidate_RefsLeaf")]
    [TestCase(@"C:\repos\foo.git\config", @"C:\repos\foo.git", TestName = "BareCandidate_GitSuffixedDirectory")]
    [TestCase(@"C:\HEAD", @"C:\", TestName = "BareCandidate_AtDriveRoot")]
    [TestCase("/repos/foo.git/description", "/repos/foo.git", TestName = "BareCandidate_PosixGitSuffixedDirectory")]
    public void TryGetBareRepositoryCandidate_BareLayoutEvidence_NamesTheDirectory(string path, string expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WatchPath.Parse(path).TryGetBareRepositoryCandidate(out var directory), Is.True);
            Assert.That(directory.Original, Is.EqualTo(expected));
        }
    }

    [TestCase(@"C:\repos\work\file.cs", TestName = "NotBareCandidate_OrdinaryFile")]
    [TestCase(@"C:\repos\work\.git", TestName = "NotBareCandidate_GitMarker")]
    [TestCase(@"C:\repos\work\.git\index", TestName = "NotBareCandidate_InsideTheMarkerDirectory")]
    [TestCase(@"C:\repos\work\notgit\file.cs", TestName = "NotBareCandidate_SuffixMustBeDotGit")]
    [TestCase("/repos/Foo.GIT/config", TestName = "NotBareCandidate_PosixSuffixIsCaseSensitive")]
    [TestCase("relative", TestName = "NotBareCandidate_NoDirectory")]
    public void TryGetBareRepositoryCandidate_OtherPaths_AreNotCandidates(string path)
    {
        Assert.That(WatchPath.Parse(path).TryGetBareRepositoryCandidate(out _), Is.False);
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
