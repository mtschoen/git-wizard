using GitWizard;

namespace GitWizardTests;

public class ByteSizeFormatterTests
{
    [TestCase(0L, "0 B")]
    [TestCase(-1L, "-1 B")]
    [TestCase(1L, "1 B")]
    [TestCase(1023L, "1023 B")]
    [TestCase(1024L, "1 KB")]
    [TestCase(1048576L, "1 MB")]
    [TestCase(1073741824L, "1 GB")]
    [TestCase(1099511627776L, "1 TB")]
    [TestCase(1536L, "1.5 KB")]
    [TestCase(1310720L, "1.25 MB")]
    [TestCase(5368709120L, "5 GB")]
    [TestCase(1125899906842624L, "1024 TB")]
    [SetCulture("de-DE")]
    public void Format_UsesInvariantCompactUnits(long bytes, string expected)
    {
        Assert.That(ByteSizeFormatter.Format(bytes), Is.EqualTo(expected));
    }

    [TestCase(null, "")]
    [TestCase(0L, "0.0 KB")]
    [TestCase(-1024L, "-1.0 KB")]
    [TestCase(1L, "0.0 KB")]
    [TestCase(512L, "0.5 KB")]
    [TestCase(1023L, "1.0 KB")]
    [TestCase(1024L, "1.0 KB")]
    [TestCase(1048575L, "1024.0 KB")]
    [TestCase(1048576L, "1.0 MB")]
    [TestCase(1073741823L, "1024.0 MB")]
    [TestCase(1073741824L, "1.0 GB")]
    [TestCase(1099511627776L, "1024.0 GB")]
    [SetCulture("en-US")]
    public void FormatForDisplay_UsesOneDecimalAndKilobyteMinimum(long? bytes, string expected)
    {
        Assert.That(ByteSizeFormatter.FormatForDisplay(bytes), Is.EqualTo(expected));
    }

    [Test]
    [SetCulture("de-DE")]
    public void FormatForDisplay_UsesCurrentCulture()
    {
        Assert.That(ByteSizeFormatter.FormatForDisplay(1536), Is.EqualTo("1,5 KB"));
    }
}
