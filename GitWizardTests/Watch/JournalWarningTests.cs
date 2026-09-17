using GitWizard.Watch;

namespace GitWizardTests.Watch;

[TestFixture]
public class JournalWarningTests
{
    [Test]
    public void VolumeArmResult_Constructors_Defaults()
    {
        var result = new VolumeArmResult(new Dictionary<string, string>(), []);
        Assert.That(result.JournalWarnings, Is.Empty);
    }
}
