using GitWizard.Watch;

namespace GitWizardTests.Watch;

[TestFixture]
public class ScanErrorChangeFilterTests
{
    [Test]
    public void TryReport_FirstErrorForDrive_ReportsIt()
    {
        var filter = new ScanErrorChangeFilter();

        Assert.That(filter.TryReport("C", "boom"), Is.True);
    }

    [TestCase("C", "boom", false)]
    [TestCase("c", "boom", false)]
    [TestCase("C", "different", true)]
    [TestCase("D", "boom", true)]
    public void TryReport_SameMessageForSameDrive_IsSilent(string subsequentDrive, string message, bool expectedResult)
    {
        var filter = new ScanErrorChangeFilter();
        filter.TryReport("C", "boom");

        Assert.That(filter.TryReport(subsequentDrive, message), Is.EqualTo(expectedResult));
    }

    [Test]
    public void Clear_ForgetsPreviousReports()
    {
        var filter = new ScanErrorChangeFilter();
        filter.TryReport("C", "boom");
        filter.Clear();

        Assert.That(filter.TryReport("C", "boom"), Is.True);
    }
}
