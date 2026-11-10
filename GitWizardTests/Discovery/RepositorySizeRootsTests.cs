using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Discovery;

/// <summary>
/// How a size summary names an index root that was not a complete snapshot, and which paths a root covers.
/// </summary>
[TestFixture]
public class RepositorySizeRootsTests
{
    static RootStatus Status(RootState state, ProducerKind? producer, bool capacityExhausted = false, int deniedSubtrees = 0)
    {
        var block = SyntheticNotifications.CreateRootBlockStatus(BlockOrigin.ProducedByScan, 1, capacityExhausted, DateTime.UtcNow) with
        {
            AccessDeniedSubtreeCount = deniedSubtrees
        };
        return SyntheticNotifications.CreateRootStatus(new VolumeKey('X'), state, block, null) with { Producer = producer };
    }

    [Test]
    public void DescribeIncompleteFields_ListsEveryReasonTheRootWasNotACompleteSnapshot()
    {
        var description = RepositorySizeRoots.DescribeIncompleteFields(
            Status(RootState.Failed, ProducerKind.Enumeration, capacityExhausted: true, deniedSubtrees: 3));

        Assert.That(description, Is.EqualTo(
            "State=Failed, Producer=Enumeration, capacity exhausted, AccessDeniedSubtreeCount=3"));
    }

    [Test]
    public void DescribeIncompleteFields_RootWithoutAProducer_NamesNone()
    {
        Assert.That(RepositorySizeRoots.DescribeIncompleteFields(Status(RootState.Ready, null)), Is.EqualTo("Producer=none"));
    }

    [Test]
    public void DescribeIncompleteFields_NothingIncomplete_IsUnspecified()
    {
        Assert.That(RepositorySizeRoots.DescribeIncompleteFields(Status(RootState.Ready, ProducerKind.Mft)), Is.EqualTo("unspecified"));
    }

    [TestCase("X", @"x:\repos\a", true, TestName = "Covers_VolumeLetterRoot_MatchesThePathsDriveCaseInsensitively")]
    [TestCase("X", @"Y:\repos\a", false, TestName = "Covers_VolumeLetterRoot_RejectsAnotherDrive")]
    [TestCase("X", "a", false, TestName = "Covers_VolumeLetterRoot_RejectsAPathWithoutADrive")]
    [TestCase(@"C:\repos", @"C:\repos\a", true, TestName = "Covers_DirectoryRoot_MatchesPathsUnderIt")]
    [TestCase(@"C:\repos", @"C:\other\a", false, TestName = "Covers_DirectoryRoot_RejectsPathsOutsideIt")]
    public void Covers_FollowsTheRootsShape(string root, string path, bool expected)
    {
        Assert.That(RepositorySizeRoots.Covers(root, path), Is.EqualTo(expected));
    }
}
