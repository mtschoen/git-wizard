using GitWizard.Watch.Linux;

namespace GitWizardTests.Watch.Linux;

[TestFixture]
public class FileHandleKeyTests
{
    [Test]
    public void SamePayloadOnDifferentFilesystems_AreDifferentKeys()
    {
        var payload = new byte[] { 1, 2, 3, 4 };

        var first = new FileHandleKey(1, payload);
        var second = new FileHandleKey(2, payload);

        Assert.Multiple(() =>
        {
            Assert.That(first.Equals(second), Is.False);
            Assert.That(first.Equals((object)second), Is.False);
        });
    }

    [Test]
    public void SamePayloadOnSameFilesystem_AreEqualKeysWithEqualHashCodes()
    {
        var first = new FileHandleKey(7, [1, 2, 3, 4]);
        var second = new FileHandleKey(7, [1, 2, 3, 4]);

        Assert.Multiple(() =>
        {
            Assert.That(first.Equals(second), Is.True);
            Assert.That(first.GetHashCode(), Is.EqualTo(second.GetHashCode()));
        });
    }

    [Test]
    public void DifferentPayloadOnSameFilesystem_AreDifferentKeys()
    {
        var first = new FileHandleKey(7, [1, 2, 3, 4]);
        var second = new FileHandleKey(7, [1, 2, 3, 5]);

        Assert.That(first.Equals(second), Is.False);
    }
}
