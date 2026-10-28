using GitWizard;
using GitWizardTests.TestSupport;
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

        Assert.That(keys.Select(key => key.ToString()), Is.EqualTo(["C", "D", @"C:\Users\someone"]));
    }

    [Test]
    public void ParseRootKeys_PreservesDirectoryScope()
    {
        using var directory = new TempDirectory();
        var keys = RootKeyProjection.ParseRootKeys([directory.Path]);

        Assert.That(keys, Is.EqualTo([RootKey.FromDirectory(directory.Path)]));
    }

    [Test]
    public async Task SessionRequiresAnObservedVolumeForDirectoryRoots()
    {
        using var files = new PackedIndexFixture();
        RootKey volume = new VolumeKey('X');
        var directory = RootKey.FromDirectory(files.TreePath);
        await using var owner = new MftIndexSession([], null, [], new Dictionary<string, string>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(owner.VolumeFor(volume), Is.EqualTo(new VolumeKey('X')));
            Assert.That(() => owner.VolumeFor(directory), Throws.InvalidOperationException);
        }
    }
}
