using GitWizard.Watch;
using GitWizard.Watch.Linux;

namespace GitWizardTests.Watch.Linux;

// Pure protocol, filter and key-comparer behavior that every platform runs.
[TestFixture]
public class LinuxWatchHelperFramingTests
{
    sealed record UnknownFrame : LinuxWatchHelperFrame;

    [Test]
    public void SerializingAFrameTheProtocolDoesNotKnow_Throws() =>
        Assert.That(() => LinuxWatchHelperProtocol.Serialize(new UnknownFrame()), Throws.ArgumentException);

    [TestCase("{\"type\":\"armResult\"}")]
    [TestCase("{\"type\":\"armResult\",\"errors\":[]}")]
    [TestCase("{\"type\":\"readiness\"}")]
    [TestCase("{\"type\":\"readiness\",\"volumes\":{}}")]
    [TestCase("{\"type\":\"batch\",\"volume\":\"/\"}")]
    [TestCase("{\"type\":\"batch\",\"entries\":[]}")]
    public void FrameMissingItsCollection_IsRejected(string line)
    {
        var parsed = LinuxWatchHelperProtocol.TryParse(line, out var frame, out var error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.False);
            Assert.That(frame, Is.Null);
            Assert.That(error, Does.Contain("incomplete"));
        }
    }

    [Test]
    public void RootFilter_RejectsAPathWithNoSeparator_AndAcceptsRootsTheirDescendantsAndSiblings()
    {
        var filter = new LinuxWatchHelperRootFilter(["/srv/repos"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter.IsRelevant("relative"), Is.False);
            Assert.That(filter.IsRelevant("/srv/repos"), Is.True);
            Assert.That(filter.IsRelevant("/srv/repos/a/.git"), Is.True);
            Assert.That(filter.IsRelevant("/srv/other"), Is.True);
            Assert.That(filter.IsRelevant("/srv/repos2/a"), Is.False);
        }
    }

    [Test]
    public void KeyComparer_TreatsNullAsDifferentFromAnyKey_AndSplitsCaseByKeySyntax()
    {
        var comparer = WatchKeyComparer.Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(comparer.Equals(null, "C"), Is.False);
            Assert.That(comparer.Equals("C", null), Is.False);
            Assert.That(comparer.Equals(null, null), Is.True);
            Assert.That(comparer.Equals("c", "C"), Is.True);
            Assert.That(comparer.Equals("/Repos", "/repos"), Is.False);
        }
    }
}
