using GitWizard;
using MFTLib.Index;

namespace GitWizardTests;

[TestFixture]
public class RootKeyProjectionTests
{
    [Test]
    public void ParseRootKeys_AcceptsEveryDriveSpecSpelling()
    {
        var keys = RootKeyProjection.ParseRootKeys(["C", "d:", @" E:\ ", "f/"]);

        Assert.That(keys.Select(key => key.ToString()), Is.EqualTo(["C", "D", "E", "F"]));
    }

    [Test]
    public void ParseRootKeys_SkipsUnparseableEntriesAndKeepsFirstOfRepeats()
    {
        var keys = RootKeyProjection.ParseRootKeys(["C", "", "not a drive", "c:", "D", @"C:\Users\someone"]);

        Assert.That(keys.Select(key => key.ToString()), Is.EqualTo(["C", "D"]));
    }

    [Test]
    public void ParseRootKeys_DoesNotWidenAPathToADirectoryRoot()
    {
        var keys = RootKeyProjection.ParseRootKeys([Path.GetTempPath()]);

        Assert.That(keys, Is.Empty);
    }

    [Test]
    public void VolumeLetter_NamesTheVolumeAndRejectsDirectoryRoots()
    {
        RootKey volume = new VolumeKey('X');
        var directory = RootKey.FromDirectory(Path.GetTempPath());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(volume.VolumeLetter(), Is.EqualTo('X'));
            Assert.That(() => directory.VolumeLetter(), Throws.InvalidOperationException);
        }
    }
}
