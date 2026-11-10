using GitWizard.Watch;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Discovery;

/// <summary>
/// The Hidden-attribute probe for routes without an index: it exists only for Windows paths on
/// Windows, and an ancestor it cannot read is simply not hidden.
/// </summary>
[TestFixture]
public class DiskHiddenAncestorsTests
{
    [Test]
    public void For_PosixPath_HasNoHiddenAttributeToProbe()
    {
        Assert.That(DiskHiddenAncestors.For(WatchPath.Parse("/srv/repos/work")), Is.Null);
    }

    [Test, Platform("Win")]
    public void For_AncestorsThatDoNotExist_AreNotHidden()
    {
        using var directory = new TempDirectory("gw-hidden-", create: false);
        var probe = DiskHiddenAncestors.For(WatchPath.Parse(Path.Combine(directory.Path, "missing", "repo")));

        Assert.That(probe!(3), Is.False);
    }

    [Test, Platform("Win")]
    public void For_HiddenAncestorWithinTheDepth_IsReported()
    {
        using var directory = new TempDirectory("gw-hidden-");
        var hidden = Path.Combine(directory.Path, "hidden");
        var repository = Path.Combine(hidden, "repo");
        Directory.CreateDirectory(repository);
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var probe = DiskHiddenAncestors.For(WatchPath.Parse(repository));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(probe!(1), Is.False, "Only the repository directory itself is within one level.");
            Assert.That(probe(2), Is.True);
        }
    }
}
